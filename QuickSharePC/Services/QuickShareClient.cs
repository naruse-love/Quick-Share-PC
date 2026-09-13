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
    /// <summary>
    /// QuickShare Protocol v300 Client implementation for Windows Desktop.
    /// Connects to a remote QuickShare server (Android or PC), performs protocol handshake,
    /// and orchestrates bi-directional high-speed LAN streaming file transfers.
    /// </summary>
    public class QuickShareClient
    {
        private TcpClient? _controlClient;
        private QuickShareStream? _ctChannel;
        private readonly List<TransferConnection> _connections = new List<TransferConnection>();
        private readonly List<TcpClient> _dataClients = new List<TcpClient>();
        private readonly BlockingCollection<byte[]> _buffers = new BlockingCollection<byte[]>();
        private readonly SemaphoreSlim _controlLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource? _cts;

        public bool IsConnected => _controlClient != null && _controlClient.Connected;
        public string ConnectedServerIp { get; private set; } = string.Empty;
        public int ConnectedServerPort { get; private set; } = 0;
        public int RemoteFileSystem { get; private set; } = QuickShareDirectory.FILE_SYSTEM_UNIX;
        public string RemoteHomeDir { get; private set; } = string.Empty;
        public string SaveDirectory { get; set; } = AppConfig.GetDefaultSaveDirectory();
        public bool Enable4KFriendly { get; set; } = false;

        // Connection & Status Events
        public event Action<string>? OnConnected;
        public event Action? OnDisconnected;
        public event Action<string>? OnStatusChanged;
        public event Action<string>? OnLogMessage;

        // Transfer Events
        public event Action<TransferTask>? OnTransferStarted;
        public event Action<TransferTask>? OnTransferProgress;
        public event Action<TransferTask>? OnTransferCompleted;

        private void Log(string msg)
        {
            OnLogMessage?.Invoke(msg);
        }

        /// <summary>
        /// Connects to a remote QuickShare server endpoint and performs protocol handshake.
        /// </summary>
        public async Task<bool> ConnectAsync(string targetIp, int targetPort = QuickShareConstants.DEFAULT_PORT, int timeoutMs = 5000)
        {
            if (string.IsNullOrWhiteSpace(targetIp))
            {
                throw new ArgumentException("服务端 IP 地址不能为空", nameof(targetIp));
            }

            Disconnect();

            OnStatusChanged?.Invoke("正在连接...");
            Log($"正在连接服务端 {targetIp}:{targetPort}...");

            var ctrlClient = new TcpClient { NoDelay = true };

            try
            {
                var connectTask = ctrlClient.ConnectAsync(targetIp, targetPort);
                if (await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) != connectTask)
                {
                    ctrlClient.Close();
                    throw new TimeoutException($"连接服务端 {targetIp}:{targetPort} 超时 (已等待 {timeoutMs}ms)。");
                }
                await connectTask;

                var stream = ctrlClient.GetStream();
                var ctChannel = new QuickShareStream(stream);

                // Step 1: Send Header "HFXC" & Version Code (300)
                byte[] headerBytes = Encoding.UTF8.GetBytes(QuickShareConstants.CLIENT_HEADER);
                await ctChannel.BaseStream.WriteAsync(headerBytes, 0, headerBytes.Length);
                ctChannel.WriteInt(QuickShareConstants.VERSION_CODE);
                await ctChannel.BaseStream.FlushAsync();

                // Step 2: Version Match Check
                bool versionMatched = await ctChannel.ReadBooleanAsync();
                if (!versionMatched)
                {
                    int serverVersion = await ctChannel.ReadIntAsync();
                    ctrlClient.Close();
                    throw new InvalidOperationException($"协议版本不匹配: 服务端版本为 {serverVersion}，当前客户端版本为 {QuickShareConstants.VERSION_CODE}。");
                }

                // Step 3: Read Advertised Interface(s)
                int serverNicCount = await ctChannel.ReadIntAsync();
                if (serverNicCount <= 0)
                {
                    ctrlClient.Close();
                    throw new InvalidOperationException("服务端未通告任何网络接口。");
                }

                var advertisedNics = new List<(string Name, IPAddress Address)>();
                for (int i = 0; i < serverNicCount; i++)
                {
                    string nicName = await ctChannel.ReadUTFAsync();
                    byte ipLen = (byte)ctChannel.BaseStream.ReadByte();
                    byte[] ipBytes = new byte[ipLen];
                    await ctChannel.ReadFullyAsync(ipBytes, 0, ipLen);
                    byte bindFlag = (byte)ctChannel.BaseStream.ReadByte();
                    advertisedNics.Add((nicName, new IPAddress(ipBytes)));
                }

                // Step 4: Connect Pure LAN Data Channel
                ctChannel.WriteBoolean(true); // clientSucceed
                ctChannel.WriteUTF("LAN");
                await ctChannel.BaseStream.FlushAsync();

                // Resolve data channel target IP
                IPAddress dataTargetIp = advertisedNics[0].Address;
                bool isWildcard = dataTargetIp.Equals(IPAddress.Any) || dataTargetIp.Equals(IPAddress.None);
                if (isWildcard || IPAddress.TryParse(targetIp, out var parsedIp) && IPAddress.IsLoopback(parsedIp))
                {
                    dataTargetIp = IPAddress.Parse(targetIp);
                }

                var dataClient = new TcpClient
                {
                    NoDelay = true,
                    ReceiveBufferSize = 4 * 1024 * 1024,
                    SendBufferSize = 4 * 1024 * 1024
                };

                var dataConnectTask = dataClient.ConnectAsync(dataTargetIp, targetPort);
                if (await Task.WhenAny(dataConnectTask, Task.Delay(timeoutMs)) != dataConnectTask)
                {
                    if (!dataTargetIp.ToString().Equals(targetIp))
                    {
                        dataClient.Close();
                        dataClient = new TcpClient
                        {
                            NoDelay = true,
                            ReceiveBufferSize = 4 * 1024 * 1024,
                            SendBufferSize = 4 * 1024 * 1024
                        };
                        dataConnectTask = dataClient.ConnectAsync(targetIp, targetPort);
                        if (await Task.WhenAny(dataConnectTask, Task.Delay(timeoutMs)) != dataConnectTask)
                        {
                            dataClient.Close();
                            ctrlClient.Close();
                            throw new TimeoutException($"连接数据传输通道到 {targetIp}:{targetPort} 超时。");
                        }
                    }
                    else
                    {
                        dataClient.Close();
                        ctrlClient.Close();
                        throw new TimeoutException($"连接数据传输通道到 {dataTargetIp}:{targetPort} 超时。");
                    }
                }
                await dataConnectTask;

                bool serverAccepted = await ctChannel.ReadBooleanAsync();
                if (!serverAccepted)
                {
                    dataClient.Close();
                    ctrlClient.Close();
                    throw new InvalidOperationException("服务端拒绝了数据通道连接。");
                }

                lock (_connections)
                {
                    _connections.Clear();
                    _connections.Add(new TransferConnection("LAN", new QuickShareStream(dataClient.GetStream())));
                }
                lock (_dataClients)
                {
                    _dataClients.Clear();
                    _dataClients.Add(dataClient);
                }

                // Step 5: Buffer Negotiation
                int serverBufCount = await ctChannel.ReadIntAsync();
                int localPoolSize = serverBufCount > 0 ? serverBufCount : 8;

                while (_buffers.TryTake(out _)) { }
                for (int i = 0; i < localPoolSize; i++)
                {
                    _buffers.Add(new byte[FileBlock.BLOCK_SIZE]);
                }

                ctChannel.WriteBoolean(true); // client buffer ok
                await ctChannel.BaseStream.FlushAsync();

                bool serverBufferOk = await ctChannel.ReadBooleanAsync();
                if (!serverBufferOk)
                {
                    ctrlClient.Close();
                    throw new InvalidOperationException("服务端缓冲区分配失败。");
                }

                // Step 6: Client File System Info & Save Directory
                int localFs = QuickShareDirectory.GetCurrentFileSystem();
                ctChannel.WriteInt(localFs);
                ctChannel.WriteUTF(SaveDirectory);
                await ctChannel.BaseStream.FlushAsync();

                // State Initialization
                _controlClient = ctrlClient;
                _ctChannel = ctChannel;
                ConnectedServerIp = targetIp;
                ConnectedServerPort = targetPort;

                string serverNicName = advertisedNics[0].Name;
                if (serverNicName.StartsWith("win|", StringComparison.OrdinalIgnoreCase))
                {
                    RemoteFileSystem = QuickShareDirectory.FILE_SYSTEM_WINDOWS;
                    var parts = serverNicName.Split('|');
                    if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        RemoteHomeDir = parts[1];
                    }
                    else
                    {
                        RemoteHomeDir = AppConfig.GetDefaultSaveDirectory();
                    }
                }
                else if (serverNicName.Contains("win", StringComparison.OrdinalIgnoreCase))
                {
                    RemoteFileSystem = QuickShareDirectory.FILE_SYSTEM_WINDOWS;
                    RemoteHomeDir = AppConfig.GetDefaultSaveDirectory();
                }
                else
                {
                    RemoteFileSystem = QuickShareDirectory.FILE_SYSTEM_UNIX;
                    RemoteHomeDir = "/sdcard/Download";
                }

                _cts = new CancellationTokenSource();
                OnConnected?.Invoke(targetIp);
                OnStatusChanged?.Invoke($"已连接: {targetIp}:{targetPort}");
                Log($"客户端成功连接至服务端 {targetIp}:{targetPort}，局域网高速传输就绪！");

                // Start background control loop for passive RPC requests from server
                _ = Task.Run(() => ControlLoopAsync(_cts.Token));
                return true;
            }
            catch (Exception ex)
            {
                ctrlClient.Close();
                Disconnect();
                OnStatusChanged?.Invoke("连接失败");
                Log($"连接服务端发生异常: {ex.Message}");
                throw;
            }
        }

        public void Disconnect()
        {
            _cts?.Cancel();

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

            lock (_connections)
            {
                foreach (var conn in _connections)
                {
                    try { conn.Close(); } catch { }
                }
                _connections.Clear();
            }

            lock (_dataClients)
            {
                foreach (var client in _dataClients)
                {
                    try { client.Close(); } catch { }
                }
                _dataClients.Clear();
            }

            while (_buffers.TryTake(out _)) { }

            ConnectedServerIp = string.Empty;
            ConnectedServerPort = 0;
            RemoteFileSystem = QuickShareDirectory.FILE_SYSTEM_UNIX;
            RemoteHomeDir = string.Empty;

            OnDisconnected?.Invoke();
            OnStatusChanged?.Invoke("未连接");
        }

        private async Task ControlLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                try
                {
                    if (_controlClient == null || !_controlClient.Connected) break;

                    if (!_controlClient.Client.Poll(50000, SelectMode.SelectRead))
                    {
                        await Task.Delay(10, ct);
                        continue;
                    }

                    await _controlLock.WaitAsync(ct);
                    try
                    {
                        if (_ctChannel == null || _controlClient == null || !_controlClient.Connected) break;

                        if (_controlClient.Client.Available == 0)
                        {
                            // If socket signaled SelectRead and Available is 0, verify if remote sent FIN (EOF)
                            if (_controlClient.Client.Poll(0, SelectMode.SelectRead))
                            {
                                Log("服务端已断开连接。");
                                break;
                            }
                            // Otherwise data was consumed by an active operation running on another thread
                            continue;
                        }

                        short opCode = await _ctChannel.ReadShortAsync();

                        switch (opCode)
                        {
                            case QuickShareConstants.SHUTDOWN:
                                Log("收到服务端断开通知。");
                                Disconnect();
                                return;

                            case QuickShareConstants.LIST_FILES:
                                await HandleRpcListFilesAsync();
                                break;

                            case QuickShareConstants.DELETE_FILE:
                                await HandleRpcDeleteFileAsync();
                                break;

                            case QuickShareConstants.MKDIR:
                                await HandleRpcMkdirAsync();
                                break;

                            case QuickShareConstants.REQUEST_RECEIVE:
                                await HandleRpcPushReceiveAsync();
                                break;

                            case QuickShareConstants.REQUEST_SEND:
                                await HandleRpcPullSendAsync();
                                break;
                        }
                    }
                    finally
                    {
                        if (_controlLock.CurrentCount == 0)
                        {
                            _controlLock.Release();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (EndOfStreamException)
                {
                    Log("服务端断开了控制通道。");
                    break;
                }
                catch (SocketException sex) when (sex.SocketErrorCode == SocketError.Interrupted || sex.ErrorCode == 10004 ||
                                                  sex.SocketErrorCode == SocketError.ConnectionReset || sex.SocketErrorCode == SocketError.ConnectionAborted)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (IsConnected)
                    {
                        Log($"控制通道处理异常: {ex.Message}");
                    }
                    break;
                }
            }

            Disconnect();
        }

        private async Task HandleRpcListFilesAsync()
        {
            try
            {
                if (_ctChannel == null) return;
                string path = await _ctChannel.ReadUTFAsync();

                var fileList = new List<RemoteFile>();

                if (string.IsNullOrWhiteSpace(path) || path == "/" || path == "\\")
                {
                    try
                    {
                        var drives = DriveInfo.GetDrives();
                        foreach (var d in drives)
                        {
                            if (!d.IsReady) continue;
                            fileList.Add(new RemoteFile(d.Name.TrimEnd('\\'), d.RootDirectory.FullName, 0, d.TotalSize, true));
                        }
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            var di = new DirectoryInfo(path);
                            foreach (var dir in di.GetDirectories())
                            {
                                if ((dir.Attributes & FileAttributes.Hidden) != 0 || (dir.Attributes & FileAttributes.System) != 0) continue;
                                fileList.Add(new RemoteFile(dir.Name, dir.FullName, new DateTimeOffset(dir.LastWriteTimeUtc).ToUnixTimeMilliseconds(), 0, true));
                            }
                            foreach (var file in di.GetFiles())
                            {
                                if ((file.Attributes & FileAttributes.Hidden) != 0 || (file.Attributes & FileAttributes.System) != 0) continue;
                                fileList.Add(new RemoteFile(file.Name, file.FullName, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(), file.Length, false));
                            }
                        }
                        else
                        {
                            _ctChannel.WriteInt(-1);
                            await _ctChannel.BaseStream.FlushAsync();
                            return;
                        }
                    }
                    catch
                    {
                        _ctChannel.WriteInt(-1);
                        await _ctChannel.BaseStream.FlushAsync();
                        return;
                    }
                }

                _ctChannel.WriteInt(fileList.Count);
                foreach (var f in fileList)
                {
                    _ctChannel.WriteUTF(f.Name);
                    _ctChannel.WriteUTF(f.Path);
                    _ctChannel.WriteLong(f.LastModified);
                    _ctChannel.WriteLong(f.Size);
                    _ctChannel.WriteBoolean(f.IsDirectory);
                }
                await _ctChannel.BaseStream.FlushAsync();
            }
            catch (Exception ex)
            {
                Log($"处理文件列表请求异常: {ex.Message}");
            }
        }

        private async Task HandleRpcDeleteFileAsync()
        {
            try
            {
                if (_ctChannel == null) return;
                string path = await _ctChannel.ReadUTFAsync();
                bool success = false;
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        success = true;
                    }
                    else if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                        success = true;
                    }
                }
                catch { }

                _ctChannel.WriteBoolean(success);
                await _ctChannel.BaseStream.FlushAsync();
            }
            catch (Exception ex)
            {
                Log($"处理删除文件请求异常: {ex.Message}");
            }
        }

        private async Task HandleRpcMkdirAsync()
        {
            try
            {
                if (_ctChannel == null) return;
                string parent = await _ctChannel.ReadUTFAsync();
                string child = await _ctChannel.ReadUTFAsync();
                bool success = false;
                try
                {
                    string target = Path.Combine(parent, child);
                    Directory.CreateDirectory(target);
                    success = true;
                }
                catch { }

                _ctChannel.WriteBoolean(success);
                await _ctChannel.BaseStream.FlushAsync();
            }
            catch (Exception ex)
            {
                Log($"处理创建文件夹请求异常: {ex.Message}");
            }
        }

        private async Task HandleRpcPushReceiveAsync()
        {
            TransferConnection primaryConn;
            lock (_connections)
            {
                if (_connections.Count == 0 || _ctChannel == null) return;
                primaryConn = _connections[0];
            }

            var task = new TransferTask
            {
                Id = Guid.NewGuid().ToString(),
                FileName = "接收服务端传输文件",
                Direction = "接收",
                Status = "传输中",
                Size = 0,
                BytesTransferred = 0
            };

            OnTransferStarted?.Invoke(task);

            var writeFileCall = new WriteFileCall(_buffers, 1, SaveDirectory);
            var writeTask = Task.Run(() => writeFileCall.ExecuteAsync());

            var speedCts = new CancellationTokenSource();
            var speedTask = Task.Run(() => SpeedMonitorAsync(task, speedCts.Token));

            var recvCall = new ReceiveFileCall(
                0,
                primaryConn,
                writeFileCall,
                (iName, path, downloaded, tot) =>
                {
                    if (task.Size < tot) task.Size = tot;
                    task.BytesTransferred = Math.Min(primaryConn.GetTotalTraffic().DownloadTraffic, task.Size);
                    task.FileName = Path.GetFileName(path);
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
                try
                {
                    if (_ctChannel != null)
                    {
                        _ctChannel.WriteBoolean(false);
                        _ctChannel.WriteUTF(ex.Message);
                        await _ctChannel.BaseStream.FlushAsync();
                    }
                }
                catch { }
                task.Status = "失败";
                Log($"写入本地文件时发生错误: {ex.Message}");
                OnTransferCompleted?.Invoke(task);
                return;
            }

            try
            {
                await receiveTask;
            }
            catch (Exception ex)
            {
                speedCts.Cancel();
                task.Status = "失败";
                Log($"网络通道接收失败: {ex.Message}");
                OnTransferCompleted?.Invoke(task);
                return;
            }

            speedCts.Cancel();

            try
            {
                if (_ctChannel != null)
                {
                    _ctChannel.WriteBoolean(true); // write ok
                    await _ctChannel.BaseStream.FlushAsync();

                    bool serverCompleteOk = await _ctChannel.ReadBooleanAsync();
                    if (serverCompleteOk)
                    {
                        task.Status = "完成";
                        Log("所有文件已成功接收并保存！");
                    }
                    else
                    {
                        task.Status = "失败";
                        Log("服务端报告传输异常。");
                    }
                }
            }
            catch { }

            OnTransferCompleted?.Invoke(task);
        }

        private async Task HandleRpcPullSendAsync()
        {
            TransferConnection primaryConn;
            lock (_connections)
            {
                if (_connections.Count == 0 || _ctChannel == null) return;
                primaryConn = _connections[0];
            }

            int count;
            var remotePaths = new List<string>();
            string remoteParentDir;
            int serverFs;
            string serverDestDir;

            try
            {
                if (_ctChannel == null) return;
                count = await _ctChannel.ReadIntAsync();
                for (int i = 0; i < count; i++)
                {
                    remotePaths.Add(await _ctChannel.ReadUTFAsync());
                }
                remoteParentDir = await _ctChannel.ReadUTFAsync();
                serverFs = await _ctChannel.ReadIntAsync();
                serverDestDir = await _ctChannel.ReadUTFAsync();
            }
            catch (Exception ex)
            {
                Log($"读取拉取请求参数异常: {ex.Message}");
                return;
            }

            var localFiles = new List<RemoteFile>();
            long totalSize = 0;
            foreach (var p in remotePaths)
            {
                if (File.Exists(p))
                {
                    var fi = new FileInfo(p);
                    localFiles.Add(new RemoteFile(fi.Name, fi.FullName, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(), fi.Length, false));
                    totalSize += fi.Length;
                }
                else if (Directory.Exists(p))
                {
                    var di = new DirectoryInfo(p);
                    localFiles.Add(new RemoteFile(di.Name, di.FullName, new DateTimeOffset(di.LastWriteTimeUtc).ToUnixTimeMilliseconds(), 0, true));
                }
            }

            var task = new TransferTask
            {
                Id = Guid.NewGuid().ToString(),
                FileName = remotePaths.Count == 1 ? Path.GetFileName(remotePaths[0]) : $"{Path.GetFileName(remotePaths[0])} 等 {remotePaths.Count} 个文件",
                Direction = "发送",
                Status = "传输中",
                Size = totalSize,
                BytesTransferred = 0
            };

            OnTransferStarted?.Invoke(task);

            string localBase = remoteParentDir;
            int localFs = (localBase.Contains(":\\") || localBase.Contains(":/")) ? QuickShareDirectory.FILE_SYSTEM_WINDOWS : QuickShareDirectory.GetCurrentFileSystem();
            var localDir = new QuickShareDirectory(localBase, localFs);
            var remoteDir = new QuickShareDirectory(serverDestDir, serverFs);

            var readFileCall = new ReadFileCall(_buffers, localFiles, localDir, remoteDir, 1, Enable4KFriendly);
            var readTask = Task.Run(() => readFileCall.ExecuteAsync());

            var speedCts = new CancellationTokenSource();
            var speedTask = Task.Run(() => SpeedMonitorAsync(task, speedCts.Token));

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

            bool serverWriteOk = false;
            try
            {
                if (_ctChannel != null)
                {
                    serverWriteOk = await _ctChannel.ReadBooleanAsync();
                }
            }
            catch (Exception ex)
            {
                Log($"读取控制通道写入反馈异常: {ex.Message}");
            }

            speedCts.Cancel();

            if (!serverWriteOk)
            {
                try
                {
                    string serverErr = _ctChannel != null ? await _ctChannel.ReadUTFAsync() : "未知错误";
                    Log($"服务端写入文件时发生错误: {serverErr}");
                }
                catch { }
                readFileCall.ShutdownByWriteError();
                task.Status = "失败";
                OnTransferCompleted?.Invoke(task);
                return;
            }

            try
            {
                bool serverChFinished = false;
                if (_ctChannel != null)
                {
                    serverChFinished = await _ctChannel.ReadBooleanAsync();
                }

                await sendTask;
                await readTask;

                if (_ctChannel != null)
                {
                    _ctChannel.WriteBoolean(serverWriteOk && serverChFinished);
                    await _ctChannel.BaseStream.FlushAsync();
                }

                task.Status = (serverWriteOk && serverChFinished) ? "完成" : "失败";
                Log("所有文件已发送成功！");
            }
            catch (Exception ex)
            {
                task.Status = "失败";
                Log($"发送文件发生异常: {ex.Message}");
            }

            OnTransferCompleted?.Invoke(task);
        }

        // --- Active Client Transfers ---

        /// <summary>
        /// Sends local files or directories to the connected remote server endpoint.
        /// </summary>
        public async Task<bool> SendFilesAsync(List<string> localPaths, string? remoteDestDir = null, Action<TransferTask>? onProgress = null)
        {
            TransferConnection primaryConn;
            lock (_connections)
            {
                if (!IsConnected || _ctChannel == null || _connections.Count == 0) return false;
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

            if (remoteFiles.Count == 0)
            {
                task.Status = "失败";
                OnTransferCompleted?.Invoke(task);
                return false;
            }

            task.Size = totalSize;
            task.Status = "传输中";
            OnTransferProgress?.Invoke(task);

            await _controlLock.WaitAsync();
            try
            {
                if (_ctChannel == null) return false;
                _ctChannel.WriteShort(QuickShareConstants.REQUEST_RECEIVE);
                await _ctChannel.BaseStream.FlushAsync();

                string localBase = Path.GetDirectoryName(localPaths[0]) ?? "";
                var localDir = new QuickShareDirectory(localBase, QuickShareDirectory.GetCurrentFileSystem());

                string targetRemoteDest = !string.IsNullOrWhiteSpace(remoteDestDir) ? remoteDestDir : RemoteHomeDir;
                if (string.IsNullOrWhiteSpace(targetRemoteDest) || targetRemoteDest == "/" || targetRemoteDest == "\\")
                {
                    targetRemoteDest = (RemoteFileSystem == QuickShareDirectory.FILE_SYSTEM_WINDOWS) ? AppConfig.GetDefaultSaveDirectory() : "/sdcard/Download";
                }
                int destFs = (targetRemoteDest.Contains(":\\") || targetRemoteDest.Contains(":/")) ? QuickShareDirectory.FILE_SYSTEM_WINDOWS : RemoteFileSystem;
                var remoteDir = new QuickShareDirectory(targetRemoteDest, destFs);

                var readFileCall = new ReadFileCall(_buffers, remoteFiles, localDir, remoteDir, 1, Enable4KFriendly);
                var readTask = Task.Run(() => readFileCall.ExecuteAsync());

                var speedCts = new CancellationTokenSource();
                var speedTask = Task.Run(() => SpeedMonitorAsync(task, speedCts.Token));

                var sendCall = new SendFileCall(
                    readFileCall,
                    primaryConn,
                    (iName, p, sent, tot) =>
                    {
                        task.BytesTransferred = Math.Min(primaryConn.GetTotalTraffic().UploadTraffic, task.Size);
                        OnTransferProgress?.Invoke(task);
                        onProgress?.Invoke(task);
                    },
                    (iName, traff, ms) => Log($"文件发送完成。已传输 {traff} 字节，耗时 {ms} 毫秒。"),
                    (iName, code, err) => Log($"传输异常中断. 错误码: {code}, 信息: {err}")
                );

                var sendTask = Task.Run(() => sendCall.ExecuteAsync());

                bool remoteReceiverOk = false;
                try
                {
                    if (_ctChannel != null)
                    {
                        remoteReceiverOk = await _ctChannel.ReadBooleanAsync();
                    }
                }
                catch (Exception ex)
                {
                    Log($"读取服务端写入反馈失败: {ex.Message}");
                }

                speedCts.Cancel();

                if (!remoteReceiverOk)
                {
                    string error = "服务端接收异常";
                    if (_ctChannel != null)
                    {
                        try { error = await _ctChannel.ReadUTFAsync(); } catch { }
                    }
                    Log($"服务端写入文件时发生错误: {error}");
                    readFileCall.ShutdownByWriteError();
                    task.Status = "失败";
                    OnTransferCompleted?.Invoke(task);
                    return false;
                }

                try
                {
                    await sendTask;
                    await readTask;

                    if (_ctChannel != null)
                    {
                        _ctChannel.WriteBoolean(true); // sender ack
                        await _ctChannel.BaseStream.FlushAsync();
                    }

                    task.Status = "完成";
                    Log("所有文件已发送成功！");
                    OnTransferCompleted?.Invoke(task);
                    return true;
                }
                catch (Exception ex)
                {
                    try
                    {
                        if (_ctChannel != null)
                        {
                            _ctChannel.WriteBoolean(false);
                            _ctChannel.WriteUTF(ex.Message);
                            await _ctChannel.BaseStream.FlushAsync();
                        }
                    }
                    catch { }
                    task.Status = "失败";
                    Log($"发送文件发生异常: {ex.Message}");
                    OnTransferCompleted?.Invoke(task);
                    return false;
                }
            }
            finally
            {
                _controlLock.Release();
            }
        }

        /// <summary>
        /// Pulls files or directories from the connected remote server endpoint.
        /// </summary>
        public async Task<bool> ReceiveFilesAsync(List<string> remotePaths, string remoteParentDir, string? localDestDir = null, Action<TransferTask>? onProgress = null)
        {
            TransferConnection primaryConn;
            lock (_connections)
            {
                if (!IsConnected || _ctChannel == null || _connections.Count == 0) return false;
                primaryConn = _connections[0];
            }

            string dest = !string.IsNullOrWhiteSpace(localDestDir) ? localDestDir : SaveDirectory;
            if (!Directory.Exists(dest))
            {
                Directory.CreateDirectory(dest);
            }

            var task = new TransferTask
            {
                Id = Guid.NewGuid().ToString(),
                FileName = remotePaths.Count == 1 ? Path.GetFileName(remotePaths[0]) : $"{Path.GetFileName(remotePaths[0])} 等 {remotePaths.Count} 个文件",
                Direction = "接收",
                Status = "传输中",
                Size = 0,
                BytesTransferred = 0
            };

            OnTransferStarted?.Invoke(task);

            await _controlLock.WaitAsync();
            try
            {
                if (_ctChannel == null) return false;
                _ctChannel.WriteShort(QuickShareConstants.REQUEST_SEND);
                _ctChannel.WriteInt(remotePaths.Count);
                foreach (var p in remotePaths)
                {
                    _ctChannel.WriteUTF(p);
                }
                _ctChannel.WriteUTF(remoteParentDir);
                _ctChannel.WriteInt(QuickShareDirectory.GetCurrentFileSystem());
                _ctChannel.WriteUTF(dest);
                await _ctChannel.BaseStream.FlushAsync();

                var writeFileCall = new WriteFileCall(_buffers, 1, dest);
                var writeTask = Task.Run(() => writeFileCall.ExecuteAsync());

                var speedCts = new CancellationTokenSource();
                var speedTask = Task.Run(() => SpeedMonitorAsync(task, speedCts.Token));

                var recvCall = new ReceiveFileCall(
                    0,
                    primaryConn,
                    writeFileCall,
                    (iName, path, downloaded, tot) =>
                    {
                        if (task.Size < tot) task.Size = tot;
                        task.BytesTransferred = Math.Min(primaryConn.GetTotalTraffic().DownloadTraffic, task.Size);
                        task.FileName = Path.GetFileName(path);
                        OnTransferProgress?.Invoke(task);
                        onProgress?.Invoke(task);
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
                    try
                    {
                        if (_ctChannel != null)
                        {
                            _ctChannel.WriteBoolean(false);
                            _ctChannel.WriteUTF(ex.Message);
                            await _ctChannel.BaseStream.FlushAsync();
                        }
                    }
                    catch { }
                    task.Status = "失败";
                    Log($"写入本地文件时发生错误: {ex.Message}");
                    OnTransferCompleted?.Invoke(task);
                    return false;
                }

                try
                {
                    await receiveTask;
                }
                catch (Exception ex)
                {
                    speedCts.Cancel();
                    try
                    {
                        if (_ctChannel != null)
                        {
                            _ctChannel.WriteBoolean(true); // write ok
                            _ctChannel.WriteBoolean(false); // receiver failed
                            await _ctChannel.BaseStream.FlushAsync();
                        }
                    }
                    catch { }
                    task.Status = "失败";
                    Log($"接收文件失败: {ex.Message}");
                    OnTransferCompleted?.Invoke(task);
                    return false;
                }

                speedCts.Cancel();

                try
                {
                    if (_ctChannel != null)
                    {
                        _ctChannel.WriteBoolean(true); // write ok
                        _ctChannel.WriteBoolean(true); // channel finish ok
                        await _ctChannel.BaseStream.FlushAsync();

                        bool senderAck = await _ctChannel.ReadBooleanAsync();
                        if (senderAck)
                        {
                            task.Status = "完成";
                            Log("拉取的文件已成功接收并保存！");
                        }
                        else
                        {
                            string err = "未知错误";
                            try { err = await _ctChannel.ReadUTFAsync(); } catch { }
                            task.Status = "失败";
                            Log($"服务端报告拉取失败: {err}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    task.Status = "失败";
                    Log($"读取控制通道拉取反馈失败: {ex.Message}");
                }

                OnTransferCompleted?.Invoke(task);
                return task.Status == "完成";
            }
            finally
            {
                _controlLock.Release();
            }
        }

        // --- Remote File Operations ---

        public async Task<List<RemoteFile>?> ListRemoteFilesAsync(string path)
        {
            if (!IsConnected || _ctChannel == null) return null;

            await _controlLock.WaitAsync();
            try
            {
                if (_ctChannel == null) return null;
                _ctChannel.WriteShort(QuickShareConstants.LIST_FILES);
                _ctChannel.WriteUTF(path);
                await _ctChannel.BaseStream.FlushAsync();

                int listSize = await _ctChannel.ReadIntAsync();
                if (listSize == -1) return null;

                var result = new List<RemoteFile>();
                for (int i = 0; i < listSize; i++)
                {
                    result.Add(new RemoteFile
                    {
                        Name = await _ctChannel.ReadUTFAsync(),
                        Path = await _ctChannel.ReadUTFAsync(),
                        LastModified = await _ctChannel.ReadLongAsync(),
                        Size = await _ctChannel.ReadLongAsync(),
                        IsDirectory = await _ctChannel.ReadBooleanAsync()
                    });
                }
                return result;
            }
            catch (Exception ex)
            {
                Log($"列出远程文件异常: {ex.Message}");
                return null;
            }
            finally
            {
                _controlLock.Release();
            }
        }

        public async Task<bool> DeleteRemoteFileAsync(string path)
        {
            if (!IsConnected || _ctChannel == null) return false;

            await _controlLock.WaitAsync();
            try
            {
                if (_ctChannel == null) return false;
                _ctChannel.WriteShort(QuickShareConstants.DELETE_FILE);
                _ctChannel.WriteUTF(path);
                await _ctChannel.BaseStream.FlushAsync();
                return await _ctChannel.ReadBooleanAsync();
            }
            catch
            {
                return false;
            }
            finally
            {
                _controlLock.Release();
            }
        }

        public async Task<bool> CreateRemoteDirAsync(string parent, string child)
        {
            if (!IsConnected || _ctChannel == null) return false;

            await _controlLock.WaitAsync();
            try
            {
                if (_ctChannel == null) return false;
                _ctChannel.WriteShort(QuickShareConstants.MKDIR);
                _ctChannel.WriteUTF(parent);
                _ctChannel.WriteUTF(child);
                await _ctChannel.BaseStream.FlushAsync();
                return await _ctChannel.ReadBooleanAsync();
            }
            catch
            {
                return false;
            }
            finally
            {
                _controlLock.Release();
            }
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
