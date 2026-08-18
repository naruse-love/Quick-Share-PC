using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickShare.PC.Models;

namespace QuickShare.PC.Services
{
    public class QuickShareServer
    {
        private readonly NetworkService _networkService;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private bool _isListening;
        private readonly BlockingCollection<byte[]> _buffers = new BlockingCollection<byte[]>();

        // Control channel
        private TcpClient? _controlClient;
        private QuickShareStream? _ctChannel;

        // Data transfer connection (Single LAN stream)
        private readonly List<TransferConnection> _connections = new List<TransferConnection>();

        public bool IsConnected => _controlClient != null && _controlClient.Connected;
        public string ConnectedDeviceIP { get; private set; } = string.Empty;
        public int RemoteFileSystem { get; private set; }
        public string RemoteHomeDir { get; private set; } = string.Empty;

        // Events
        public event Action<string>? OnDeviceConnected;
        public event Action? OnDeviceDisconnected;
        public event Action<string>? OnStatusChanged;
        public event Action<string>? OnLogMessage;

        // Transfer Events
        public event Action<TransferTask>? OnTransferStarted;
        public event Action<TransferTask>? OnTransferProgress;
        public event Action<TransferTask>? OnTransferCompleted;

        public QuickShareServer(NetworkService? networkService = null)
        {
            _networkService = networkService ?? new NetworkService();
        }

        private void Log(string msg)
        {
            OnLogMessage?.Invoke(msg);
        }

        public void Start(int port)
        {
            if (_isListening) return;

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _isListening = true;
            OnStatusChanged?.Invoke($"正在监听 端口 {port}");
            Log($"服务端已启动，正在监听端口 {port}...");

            Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        // Backward-compatible overload
        public void Start(int port, List<NetworkInterfaceInfo>? selectedInterfaces)
        {
            Start(port);
        }

        public void Stop()
        {
            _isListening = false;
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;

            DisconnectCurrentDevice();
            OnStatusChanged?.Invoke("已停止");
            Log("服务端已停止。");
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isListening)
            {
                try
                {
                    TcpClient client = await _listener!.AcceptTcpClientAsync(ct);
                    client.NoDelay = true;
                    Log($"收到来自 {client.Client.RemoteEndPoint} 的控制通道连接请求。");

                    if (IsConnected)
                    {
                        Log("已有设备连接，拒绝新连接。");
                        client.Close();
                        continue;
                    }

                    ConnectedDeviceIP = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                    bool handshakeSuccess = await PerformHandshakeAsync(client);
                    if (handshakeSuccess)
                    {
                        Log("设备握手成功，局域网高速传输通道已就绪！");
                        OnDeviceConnected?.Invoke(ConnectedDeviceIP);
                        OnStatusChanged?.Invoke($"已连接: {ConnectedDeviceIP}");
                    }
                    else
                    {
                        Log("设备握手失败。");
                        DisconnectCurrentDevice();
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Accept 循环发生异常: {ex.Message}");
                    DisconnectCurrentDevice();
                }
            }
        }

        private async Task<bool> PerformHandshakeAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                _ctChannel = new QuickShareStream(stream);
                _controlClient = client;

                // 1. Read header HFXC
                byte[] header = new byte[4];
                await _ctChannel.ReadFullyAsync(header, 0, 4);
                string headerStr = Encoding.UTF8.GetString(header);
                if (headerStr != QuickShareConstants.CLIENT_HEADER)
                {
                    Log($"非法头部魔数: {headerStr}");
                    return false;
                }

                // 2. Read version
                int versionCode = await _ctChannel.ReadIntAsync();
                if (versionCode != QuickShareConstants.VERSION_CODE)
                {
                    Log($"协议版本不匹配。客户端版本: {versionCode}, 服务端支持版本: {QuickShareConstants.VERSION_CODE}");
                    _ctChannel.WriteBoolean(false);
                    _ctChannel.WriteInt(QuickShareConstants.VERSION_CODE);
                    await _ctChannel.BaseStream.FlushAsync();
                    return false;
                }

                _ctChannel.WriteBoolean(true); // Version matched
                await _ctChannel.BaseStream.FlushAsync();

                // 3. Advertise 1 primary LAN interface (serverNicCount = 1)
                string localIp = _networkService.GetPrimaryLanIpAddress();
                var primaryNic = _networkService.GetPrimaryLanInterface();
                string nicName = !string.IsNullOrEmpty(primaryNic.Name) ? primaryNic.Name : "LAN";

                _ctChannel.WriteInt(1); // serverNicCount = 1
                _ctChannel.WriteUTF(nicName);
                var addrBytes = IPAddress.Parse(localIp).GetAddressBytes();
                _ctChannel.WriteByte((byte)addrBytes.Length);
                _ctChannel.BaseStream.Write(addrBytes, 0, addrBytes.Length);
                _ctChannel.WriteByte(0); // clientBindAddress is null -> 0
                await _ctChannel.BaseStream.FlushAsync();

                // 4. Accept exactly 1 data socket connection for pure LAN high-speed streaming
                lock (_connections)
                {
                    _connections.Clear();
                }
                bool clientSucceed = await _ctChannel.ReadBooleanAsync();
                string clientInterfaceName = await _ctChannel.ReadUTFAsync();

                if (clientSucceed)
                {
                    Log($"等待客户端连接数据传输通道 ({clientInterfaceName})...");
                    TcpClient transClient = await _listener!.AcceptTcpClientAsync(_cts!.Token);
                    transClient.NoDelay = true;
                    transClient.ReceiveBufferSize = 1024 * 1024;
                    transClient.SendBufferSize = 1024 * 1024;

                    lock (_connections)
                    {
                        _connections.Add(new TransferConnection(clientInterfaceName, new QuickShareStream(transClient.GetStream())));
                    }
                    _ctChannel.WriteBoolean(true);
                    await _ctChannel.BaseStream.FlushAsync();
                    Log($"数据传输通道已建立: {clientInterfaceName}");
                }
                else
                {
                    _ctChannel.WriteBoolean(false);
                    await _ctChannel.BaseStream.FlushAsync();
                    return false;
                }

                // 5. Buffer Negotiation (8 blocks of 1MB)
                int localBufferCount = 8;
                _ctChannel.WriteInt(localBufferCount);
                await _ctChannel.BaseStream.FlushAsync();

                bool remoteBufferOk = await _ctChannel.ReadBooleanAsync();
                if (!remoteBufferOk)
                {
                    Log("手机端分配内存缓冲失败。");
                    return false;
                }

                // Allocate local 1MB buffers
                while (_buffers.TryTake(out _)) { }
                for (int i = 0; i < localBufferCount; i++)
                {
                    _buffers.Add(new byte[FileBlock.BLOCK_SIZE]);
                }
                _ctChannel.WriteBoolean(true);
                await _ctChannel.BaseStream.FlushAsync();

                // 6. Read client file system info
                RemoteFileSystem = await _ctChannel.ReadIntAsync();
                RemoteHomeDir = await _ctChannel.ReadUTFAsync();

                return true;
            }
            catch (Exception ex)
            {
                Log($"握手过程发生错误: {ex.Message}");
                return false;
            }
        }

        public void DisconnectCurrentDevice()
        {
            if (_ctChannel != null)
            {
                try
                {
                    _ctChannel.WriteShort(QuickShareConstants.SHUTDOWN);
                }
                catch { }
            }

            _ctChannel?.Close();
            _ctChannel = null;

            _controlClient?.Close();
            _controlClient = null;

            TransferConnection[] conns;
            lock (_connections)
            {
                conns = _connections.ToArray();
                _connections.Clear();
            }

            foreach (var conn in conns)
            {
                try { conn.Close(); } catch { }
            }

            while (_buffers.TryTake(out _)) { }

            ConnectedDeviceIP = string.Empty;
            RemoteFileSystem = 0;
            RemoteHomeDir = string.Empty;

            OnDeviceDisconnected?.Invoke();
            OnStatusChanged?.Invoke(_isListening ? "等待连接" : "已停止");
        }

        // --- Commands triggered by PC ---

        public Task<List<RemoteFile>?> ListRemoteFilesAsync(string path)
        {
            if (!IsConnected || _ctChannel == null) return Task.FromResult<List<RemoteFile>?>(null);

            lock (_ctChannel)
            {
                _ctChannel.WriteShort(QuickShareConstants.LIST_FILES);
                _ctChannel.WriteUTF(path);

                int listSize = _ctChannel.ReadInt();
                if (listSize == -1) return Task.FromResult<List<RemoteFile>?>(null);

                var result = new List<RemoteFile>();
                for (int i = 0; i < listSize; i++)
                {
                    result.Add(new RemoteFile
                    {
                        Name = _ctChannel.ReadUTF(),
                        Path = _ctChannel.ReadUTF(),
                        LastModified = _ctChannel.ReadLong(),
                        Size = _ctChannel.ReadLong(),
                        IsDirectory = _ctChannel.ReadBoolean()
                    });
                }
                return Task.FromResult<List<RemoteFile>?>(result);
            }
        }

        public Task<bool> DeleteRemoteFileAsync(string path)
        {
            if (!IsConnected || _ctChannel == null) return Task.FromResult(false);

            lock (_ctChannel)
            {
                _ctChannel.WriteShort(QuickShareConstants.DELETE_FILE);
                _ctChannel.WriteUTF(path);
                return Task.FromResult(_ctChannel.ReadBoolean());
            }
        }

        public Task<bool> CreateRemoteDirAsync(string parent, string child)
        {
            if (!IsConnected || _ctChannel == null) return Task.FromResult(false);

            lock (_ctChannel)
            {
                _ctChannel.WriteShort(QuickShareConstants.MKDIR);
                _ctChannel.WriteUTF(parent);
                _ctChannel.WriteUTF(child);
                return Task.FromResult(_ctChannel.ReadBoolean());
            }
        }

        // --- Pure LAN Single-Stream File Transfers ---

        // Send local files on PC to Remote Phone
        public async Task SendFilesToRemoteAsync(List<string> localPaths, string remoteDestDir)
        {
            TransferConnection primaryConn;
            lock (_connections)
            {
                if (!IsConnected || _ctChannel == null || _connections.Count == 0) return;
                primaryConn = _connections[0];
            }

            var task = new TransferTask
            {
                Id = Guid.NewGuid().ToString(),
                FileName = localPaths.Count == 1 ? Path.GetFileName(localPaths[0]) : $"{Path.GetFileName(localPaths[0])} 等 {localPaths.Count} 个文件",
                Direction = "发送",
                Status = "计算大小中",
                Size = 0,
                BytesTransferred = 0
            };

            OnTransferStarted?.Invoke(task);

            // Compute total size and collect metadata
            var remoteFiles = new List<RemoteFile>();
            long totalSize = 0;
            foreach (var path in localPaths)
            {
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    remoteFiles.Add(new RemoteFile(fi.Name, fi.FullName, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(), fi.Length, false));
                    totalSize += fi.Length;
                }
                else if (Directory.Exists(path))
                {
                    var di = new DirectoryInfo(path);
                    remoteFiles.Add(new RemoteFile(di.Name, di.FullName, new DateTimeOffset(di.LastWriteTimeUtc).ToUnixTimeMilliseconds(), 0, true));
                }
            }

            task.Size = totalSize;
            task.Status = "传输中";
            OnTransferProgress?.Invoke(task);

            // Request client to receive
            lock (_ctChannel)
            {
                _ctChannel.WriteShort(QuickShareConstants.REQUEST_RECEIVE);
            }

            var localDir = new QuickShareDirectory(Path.GetDirectoryName(localPaths[0]) ?? "", QuickShareDirectory.GetCurrentFileSystem());
            var remoteDir = new QuickShareDirectory(remoteDestDir, RemoteFileSystem);

            var readFileCall = new ReadFileCall(_buffers, remoteFiles, localDir, remoteDir, 1);
            var readTask = Task.Run(() => readFileCall.ExecuteAsync());

            // Speed Monitor
            var speedCts = new CancellationTokenSource();
            var speedTask = Task.Run(() => SpeedMonitorAsync(task, speedCts.Token));

            // Single LAN stream send task
            var sendCall = new SendFileCall(
                readFileCall,
                primaryConn,
                (iName, p, sent, tot) =>
                {
                    task.BytesTransferred = Math.Min(primaryConn.GetTotalTraffic().UploadTraffic, task.Size);
                    OnTransferProgress?.Invoke(task);
                },
                (iName, traff, ms) => Log($"文件发送完成。已传输 {traff} 字节，耗时 {ms} 毫秒。"),
                (iName, code, err) => Log($"传输异常中断. 错误码: {code}, 信息: {err}")
            );

            var sendTask = Task.Run(() => sendCall.ExecuteAsync());

            // Wait for client to write complete or error on control channel
            bool clientOk = false;
            try
            {
                clientOk = await _ctChannel.ReadBooleanAsync();
            }
            catch (Exception ex)
            {
                Log($"读取控制通道写入反馈失败: {ex.Message}");
            }

            speedCts.Cancel();

            if (!clientOk)
            {
                string clientError = await _ctChannel.ReadUTFAsync();
                Log($"手机端写入文件时发生错误: {clientError}");
                readFileCall.ShutdownByWriteError();
                task.Status = "失败";
                OnTransferCompleted?.Invoke(task);
                return;
            }

            try
            {
                await sendTask;
                await readTask;
                _ctChannel.WriteBoolean(true);
                task.Status = "完成";
                Log("所有文件已发送成功！");
            }
            catch (Exception ex)
            {
                _ctChannel.WriteBoolean(false);
                _ctChannel.WriteUTF(ex.Message);
                task.Status = "失败";
                Log($"发送文件发生异常: {ex.Message}");
            }

            OnTransferCompleted?.Invoke(task);
        }

        // Pull files from Remote Phone to PC
        public async Task PullFilesFromRemoteAsync(List<string> remotePaths, string localDestDir)
        {
            TransferConnection primaryConn;
            lock (_connections)
            {
                if (!IsConnected || _ctChannel == null || _connections.Count == 0) return;
                primaryConn = _connections[0];
            }

            var task = new TransferTask
            {
                Id = Guid.NewGuid().ToString(),
                FileName = remotePaths.Count == 1 ? Path.GetFileName(remotePaths[0]) : $"{Path.GetFileName(remotePaths[0])} 等 {remotePaths.Count} 个文件",
                Direction = "接收",
                Status = "初始化中",
                Size = 0,
                BytesTransferred = 0
            };

            OnTransferStarted?.Invoke(task);

            // 1. Tell client we want to request files (REQUEST_SEND)
            lock (_ctChannel)
            {
                _ctChannel.WriteShort(QuickShareConstants.REQUEST_SEND);
                _ctChannel.WriteInt(remotePaths.Count);
                foreach (var path in remotePaths)
                {
                    _ctChannel.WriteUTF(path);
                }

                // localDir from remote's perspective is localDir (source on phone)
                string remoteParentDir = Path.GetDirectoryName(remotePaths[0])?.Replace('\\', '/') ?? "/";
                _ctChannel.WriteUTF(remoteParentDir);
                _ctChannel.WriteInt(RemoteFileSystem);

                // remoteDir from remote's perspective is remoteDir (destination on PC)
                _ctChannel.WriteUTF(localDestDir);
            }

            task.Status = "传输中";
            OnTransferProgress?.Invoke(task);

            var writeFileCall = new WriteFileCall(_buffers, 1);
            var writeTask = Task.Run(() => writeFileCall.ExecuteAsync());

            // Speed Monitor
            var speedCts = new CancellationTokenSource();
            var speedTask = Task.Run(() => SpeedMonitorAsync(task, speedCts.Token));

            var recvCall = new ReceiveFileCall(
                0,
                primaryConn,
                writeFileCall,
                (iName, path, downloaded, tot) =>
                {
                    if (task.Size < tot)
                    {
                        task.Size = tot;
                    }
                    task.BytesTransferred = Math.Min(primaryConn.GetTotalTraffic().DownloadTraffic, task.Size);
                    OnTransferProgress?.Invoke(task);
                },
                (iName, traff, ms) => Log($"文件接收完成。已传输 {traff} 字节，耗时 {ms} 毫秒。"),
                (iName, code, err) => Log($"接收异常中断. 错误码: {code}, 信息: {err}")
            );

            var receiveTask = Task.Run(() => recvCall.ExecuteAsync());

            try
            {
                await writeTask;
            }
            catch (Exception ex)
            {
                speedCts.Cancel();
                _ctChannel.WriteBoolean(false);
                _ctChannel.WriteUTF(ex.Message);
                task.Status = "失败";
                Log($"写入本地文件时发生错误: {ex.Message}");
                OnTransferCompleted?.Invoke(task);
                return;
            }

            // Wait for receiver to complete
            try
            {
                await receiveTask;
            }
            catch (Exception ex)
            {
                speedCts.Cancel();
                _ctChannel.WriteBoolean(true); // acknowledge write ok, but receiver failed
                task.Status = "失败";
                Log($"网络通道接收失败: {ex.Message}");
                OnTransferCompleted?.Invoke(task);
                return;
            }

            speedCts.Cancel();
            _ctChannel.WriteBoolean(true); // receivers finished ok

            bool clientCompleteOk = false;
            try
            {
                clientCompleteOk = await _ctChannel.ReadBooleanAsync();
            }
            catch (Exception ex)
            {
                Log($"读取控制通道接收反馈失败: {ex.Message}");
            }

            if (clientCompleteOk)
            {
                task.Status = "完成";
                Log("所有文件已成功接收并保存！");
            }
            else
            {
                string clientError = await _ctChannel.ReadUTFAsync();
                task.Status = "失败";
                Log($"手机端读取文件发生错误: {clientError}");
            }

            OnTransferCompleted?.Invoke(task);
        }

        private async Task SpeedMonitorAsync(TransferTask task, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, ct);

                    long totalSpeed = 0;
                    TransferConnection[] conns;
                    lock (_connections)
                    {
                        conns = _connections.ToArray();
                    }
                    foreach (var conn in conns)
                    {
                        var traffic = conn.ResetCurrentTrafficInfo();
                        totalSpeed += task.Direction == "发送" ? traffic.UploadTraffic : traffic.DownloadTraffic;
                    }

                    task.Speed = FormatSpeed(totalSpeed);
                    OnTransferProgress?.Invoke(task);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
            task.Speed = "0 KB/s";
            OnTransferProgress?.Invoke(task);
        }

        private string FormatSpeed(long bytesPerSec)
        {
            double speed = bytesPerSec;
            if (speed < 1024) return $"{speed:F0} B/s";
            speed /= 1024.0;
            if (speed < 1024) return $"{speed:F1} KB/s";
            speed /= 1024.0;
            if (speed < 1024) return $"{speed:F1} MB/s";
            speed /= 1024.0;
            return $"{speed:F1} GB/s";
        }
    }
}
