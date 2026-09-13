using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using QuickShare.PC.Models;
using QuickShare.PC.Services;

namespace QuickShare.PC.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ConfigService _configService;
        private readonly NetworkService _networkService;
        private readonly QuickShareServer _server;
        private readonly QuickShareClient _client;
        private AppConfig _config;

        // Mode: 0 = Server Mode, 1 = Client Mode
        private int _selectedModeIndex = 0;

        // Server properties
        private bool _isServerRunning;
        private string _serverStatusText = "已停止";
        private string _logText = "";
        private string _selectedSaveDir = "";
        private int _port = 5740;
        private bool _autoStart;
        private bool _enable4KFriendly;
        private string _connectedDevice = "未连接";
        private string _primaryLanIp = "127.0.0.1";
        private string _primaryNetworkType = "局域网";
        private string _primaryAdapterName = "本地网络";

        // Client properties
        private string _targetIp = "192.168.1.100";
        private int _targetPort = 5740;
        private bool _isClientConnected;
        private bool _isClientConnecting;
        private string _clientStatusText = "未连接";
        private string _clientConnectedDevice = "未连接";
        private string _clientRemoteFs = "未知";
        private string _clientRemoteHomeDir = "/sdcard/Download";

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<NetworkInterfaceInfo> NetworkInterfaces { get; } = new();
        public ObservableCollection<TransferTask> TransferTasks { get; } = new();
        public ObservableCollection<TransferHistory> TransferHistories { get; } = new();

        // Server Commands
        public RelayCommand ToggleServerCommand { get; }
        public RelayCommand RefreshInterfacesCommand { get; }
        public RelayCommand CopyIpCommand { get; }

        // Client Commands
        public RelayCommand ToggleClientConnectionCommand { get; }
        public RelayCommand ConnectClientCommand { get; }
        public RelayCommand DisconnectClientCommand { get; }

        // Common Commands
        public RelayCommand SwitchToServerModeCommand { get; }
        public RelayCommand SwitchToClientModeCommand { get; }
        public RelayCommand BrowseSaveDirCommand { get; }
        public RelayCommand OpenSaveDirCommand { get; }
        public RelayCommand ClearLogsCommand { get; }
        public RelayCommand SelectAndSendFilesCommand { get; }
        public RelayCommand SelectAndSendFolderCommand { get; }

        public MainViewModel(QuickShareServer server, ConfigService configService, NetworkService networkService, QuickShareClient? client = null)
        {
            _server = server;
            _configService = configService;
            _networkService = networkService;
            _client = client ?? new QuickShareClient();

            // Load Config
            _config = _configService.LoadConfig();
            SelectedSaveDir = _config.SaveDirectory;
            _server.SaveDirectory = SelectedSaveDir;
            _client.SaveDirectory = SelectedSaveDir;
            Port = _config.Port;
            AutoStart = _config.AutoStart;
            Enable4KFriendly = _config.Enable4KFriendly;
            _server.Enable4KFriendly = Enable4KFriendly;
            _client.Enable4KFriendly = Enable4KFriendly;
            TargetIp = !string.IsNullOrWhiteSpace(_config.TargetIp) ? _config.TargetIp : "192.168.1.100";
            TargetPort = _config.TargetPort > 0 ? _config.TargetPort : 5740;
            SelectedModeIndex = _config.IsClientMode ? 1 : 0;

            // Initialize commands
            ToggleServerCommand = new RelayCommand(ToggleServer);
            RefreshInterfacesCommand = new RelayCommand(RefreshNetworkInfo);
            CopyIpCommand = new RelayCommand(CopyIpToClipboard);

            ToggleClientConnectionCommand = new RelayCommand(async () => await ToggleClientConnectionAsync());
            ConnectClientCommand = new RelayCommand(async () => await ConnectClientAsync());
            DisconnectClientCommand = new RelayCommand(DisconnectClient);

            SwitchToServerModeCommand = new RelayCommand(() => SelectedModeIndex = 0);
            SwitchToClientModeCommand = new RelayCommand(() => SelectedModeIndex = 1);
            BrowseSaveDirCommand = new RelayCommand(BrowseSaveDir);
            OpenSaveDirCommand = new RelayCommand(OpenSaveDir);
            ClearLogsCommand = new RelayCommand(ClearLogs);
            SelectAndSendFilesCommand = new RelayCommand(async () => await SelectAndSendFilesAsync());
            SelectAndSendFolderCommand = new RelayCommand(async () => await SelectAndSendFolderAsync());

            // Register Server events
            _server.OnStatusChanged += status =>
            {
                SafeDispatch(() =>
                {
                    ServerStatusText = status;
                    OnPropertyChanged(nameof(ActiveConnectedStatus));
                    OnPropertyChanged(nameof(IsAnyConnected));
                });
            };

            _server.OnDeviceConnected += ip =>
            {
                SafeDispatch(() =>
                {
                    ConnectedDevice = ip;
                    OnPropertyChanged(nameof(ActiveConnectedStatus));
                    OnPropertyChanged(nameof(IsAnyConnected));
                });
            };

            _server.OnDeviceDisconnected += () =>
            {
                SafeDispatch(() =>
                {
                    ConnectedDevice = "未连接";
                    OnPropertyChanged(nameof(ActiveConnectedStatus));
                    OnPropertyChanged(nameof(IsAnyConnected));
                });
            };

            _server.OnLogMessage += msg => AppendLog(msg);

            _server.OnTransferStarted += task =>
            {
                SafeDispatch(() => TransferTasks.Insert(0, task));
            };

            _server.OnTransferProgress += task =>
            {
                // TransferTask implements INotifyPropertyChanged
            };

            _server.OnTransferCompleted += task =>
            {
                SafeDispatch(() =>
                {
                    TransferTasks.Remove(task);
                    TransferHistories.Insert(0, new TransferHistory
                    {
                        FileName = task.FileName,
                        Direction = task.Direction,
                        SizeString = FormatSize(task.Size),
                        TimeString = DateTime.Now.ToString("HH:mm:ss"),
                        Status = task.Status
                    });
                });
            };

            // Register Client events
            _client.OnStatusChanged += status =>
            {
                SafeDispatch(() =>
                {
                    ClientStatusText = status;
                    OnPropertyChanged(nameof(ActiveConnectedStatus));
                    OnPropertyChanged(nameof(IsAnyConnected));
                });
            };

            _client.OnConnected += ip =>
            {
                SafeDispatch(() =>
                {
                    IsClientConnected = true;
                    ClientConnectedDevice = $"{ip}:{TargetPort}";
                    ClientRemoteFs = _client.RemoteFileSystem == QuickShareDirectory.FILE_SYSTEM_WINDOWS ? "Windows" : "Android / Linux";
                    ClientRemoteHomeDir = _client.RemoteHomeDir;
                    OnPropertyChanged(nameof(ActiveConnectedStatus));
                    OnPropertyChanged(nameof(IsAnyConnected));
                });
            };

            _client.OnDisconnected += () =>
            {
                SafeDispatch(() =>
                {
                    IsClientConnected = false;
                    ClientConnectedDevice = "未连接";
                    ClientStatusText = "未连接";
                    OnPropertyChanged(nameof(ActiveConnectedStatus));
                    OnPropertyChanged(nameof(IsAnyConnected));
                });
            };

            _client.OnLogMessage += msg => AppendLog(msg);

            _client.OnTransferStarted += task =>
            {
                SafeDispatch(() => TransferTasks.Insert(0, task));
            };

            _client.OnTransferProgress += task =>
            {
                // TransferTask implements INotifyPropertyChanged
            };

            _client.OnTransferCompleted += task =>
            {
                SafeDispatch(() =>
                {
                    TransferTasks.Remove(task);
                    TransferHistories.Insert(0, new TransferHistory
                    {
                        FileName = task.FileName,
                        Direction = task.Direction,
                        SizeString = FormatSize(task.Size),
                        TimeString = DateTime.Now.ToString("HH:mm:ss"),
                        Status = task.Status
                    });
                });
            };

            // Load network information
            RefreshNetworkInfo();
        }

        // --- Mode Switching Properties ---

        public int SelectedModeIndex
        {
            get => _selectedModeIndex;
            set
            {
                if (_selectedModeIndex != value)
                {
                    _selectedModeIndex = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsServerMode));
                    OnPropertyChanged(nameof(IsClientMode));
                    OnPropertyChanged(nameof(ActiveConnectedStatus));

                    _config.IsClientMode = (_selectedModeIndex == 1);
                    _configService.SaveConfig(_config);
                }
            }
        }

        public bool IsServerMode
        {
            get => _selectedModeIndex == 0;
            set
            {
                if (value) SelectedModeIndex = 0;
            }
        }

        public bool IsClientMode
        {
            get => _selectedModeIndex == 1;
            set
            {
                if (value) SelectedModeIndex = 1;
            }
        }

        public bool IsAnyConnected => _server.IsConnected || _client.IsConnected;

        public string ActiveConnectedStatus
        {
            get
            {
                if (IsClientMode)
                {
                    return _client.IsConnected
                        ? $"🟢 客户端已连接至 {_client.ConnectedServerIp}:{_client.ConnectedServerPort} - 随时可发送文件"
                        : "⚪ 客户端未连接 - 请输入对方 IP 并点击连接";
                }
                else
                {
                    return _server.IsConnected
                        ? $"🟢 服务端已接入设备 {_server.ConnectedDeviceIP} - 随时可发送文件"
                        : (IsServerRunning ? "🟡 服务已启动 - 等待手机/对方客户端连接" : "⚪ 服务已停止 - 请点击启动服务");
                }
            }
        }

        // --- Server Mode Properties ---

        public bool IsServerRunning
        {
            get => _isServerRunning;
            set
            {
                _isServerRunning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveConnectedStatus));
            }
        }

        public string ServerStatusText
        {
            get => _serverStatusText;
            set
            {
                _serverStatusText = value;
                OnPropertyChanged();
            }
        }

        public string ConnectedDevice
        {
            get => _connectedDevice;
            set
            {
                _connectedDevice = value;
                OnPropertyChanged();
            }
        }

        public string PrimaryLanIp
        {
            get => _primaryLanIp;
            set
            {
                _primaryLanIp = value;
                OnPropertyChanged();
            }
        }

        public string PrimaryNetworkType
        {
            get => _primaryNetworkType;
            set
            {
                _primaryNetworkType = value;
                OnPropertyChanged();
            }
        }

        public string PrimaryAdapterName
        {
            get => _primaryAdapterName;
            set
            {
                _primaryAdapterName = value;
                OnPropertyChanged();
            }
        }

        // --- Client Mode Properties ---

        public string TargetIp
        {
            get => _targetIp;
            set
            {
                _targetIp = value;
                OnPropertyChanged();
                _config.TargetIp = _targetIp;
                _configService.SaveConfig(_config);
            }
        }

        public int TargetPort
        {
            get => _targetPort;
            set
            {
                _targetPort = value;
                OnPropertyChanged();
                _config.TargetPort = _targetPort;
                _configService.SaveConfig(_config);
            }
        }

        public bool IsClientConnected
        {
            get => _isClientConnected;
            set
            {
                _isClientConnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsAnyConnected));
                OnPropertyChanged(nameof(ActiveConnectedStatus));
            }
        }

        public bool IsClientConnecting
        {
            get => _isClientConnecting;
            set
            {
                _isClientConnecting = value;
                OnPropertyChanged();
            }
        }

        public string ClientStatusText
        {
            get => _clientStatusText;
            set
            {
                _clientStatusText = value;
                OnPropertyChanged();
            }
        }

        public string ClientConnectedDevice
        {
            get => _clientConnectedDevice;
            set
            {
                _clientConnectedDevice = value;
                OnPropertyChanged();
            }
        }

        public string ClientRemoteFs
        {
            get => _clientRemoteFs;
            set
            {
                _clientRemoteFs = value;
                OnPropertyChanged();
            }
        }

        public string ClientRemoteHomeDir
        {
            get => _clientRemoteHomeDir;
            set
            {
                _clientRemoteHomeDir = value;
                OnPropertyChanged();
            }
        }

        // --- Shared Configuration Properties ---

        public string LogText
        {
            get => _logText;
            set
            {
                _logText = value;
                OnPropertyChanged();
            }
        }

        public string SelectedSaveDir
        {
            get => _selectedSaveDir;
            set
            {
                _selectedSaveDir = value;
                _server.SaveDirectory = _selectedSaveDir;
                _client.SaveDirectory = _selectedSaveDir;
                OnPropertyChanged();
                _config.SaveDirectory = _selectedSaveDir;
                _configService.SaveConfig(_config);
            }
        }

        public int Port
        {
            get => _port;
            set
            {
                _port = value;
                OnPropertyChanged();
                _config.Port = _port;
                _configService.SaveConfig(_config);
            }
        }

        public bool AutoStart
        {
            get => _autoStart;
            set
            {
                _autoStart = value;
                OnPropertyChanged();
                _config.AutoStart = _autoStart;
                _configService.SaveConfig(_config);
                SetAutoStartRegistry(_autoStart);
            }
        }

        public bool Enable4KFriendly
        {
            get => _enable4KFriendly;
            set
            {
                if (_enable4KFriendly != value)
                {
                    _enable4KFriendly = value;
                    _server.Enable4KFriendly = value;
                    _client.Enable4KFriendly = value;
                    OnPropertyChanged();
                    _config.Enable4KFriendly = value;
                    _configService.SaveConfig(_config);
                }
            }
        }

        // --- Server Operations ---

        public void ToggleServer()
        {
            if (IsServerRunning)
            {
                try
                {
                    _server.Stop();
                }
                catch (Exception ex)
                {
                    AppendLog($"停止服务时发生异常: {ex.Message}");
                }
                IsServerRunning = false;
            }
            else
            {
                try
                {
                    _server.Start(Port);
                    IsServerRunning = true;
                }
                catch (SocketException sex)
                {
                    IsServerRunning = false;
                    ServerStatusText = "启动失败 (端口错误)";
                    string detail = sex.ErrorCode == 10013 || sex.SocketErrorCode == SocketError.AccessDenied
                        ? $"\n\n原因: 端口 {Port} 被 Windows 保留(例如 Hyper-V / WSL 端口排除范围)或权限不足。\n建议: 请修改端口为其他端口(例如 5840、57400 等)后再尝试启动。"
                        : (sex.ErrorCode == 10048 || sex.SocketErrorCode == SocketError.AddressAlreadyInUse
                            ? $"\n\n原因: 端口 {Port} 已被其他程序占用。\n建议: 请更换其他未被占用的端口。"
                            : $"\n\n错误代码: {sex.SocketErrorCode} ({sex.ErrorCode})");

                    string errorMsg = $"启动服务端失败 (端口 {Port}): {sex.Message}{detail}";
                    AppendLog(errorMsg);
                    System.Windows.MessageBox.Show(errorMsg, "服务端启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception ex)
                {
                    IsServerRunning = false;
                    ServerStatusText = "启动失败";
                    string errorMsg = $"启动服务端失败: {ex.Message}";
                    AppendLog(errorMsg);
                    System.Windows.MessageBox.Show(errorMsg, "服务端启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // --- Client Operations ---

        public async Task ToggleClientConnectionAsync()
        {
            if (IsClientConnected)
            {
                DisconnectClient();
            }
            else
            {
                await ConnectClientAsync();
            }
        }

        public async Task ConnectClientAsync()
        {
            if (IsClientConnected) return;

            string ip = TargetIp?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(ip))
            {
                System.Windows.MessageBox.Show("请输入目标服务端的 IP 地址！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsClientConnecting = true;
            ClientStatusText = "正在连接...";

            try
            {
                await _client.ConnectAsync(ip, TargetPort);
            }
            catch (Exception ex)
            {
                string msg = $"连接远程服务端失败: {ex.Message}";
                AppendLog(msg);
                System.Windows.MessageBox.Show(msg, "客户端连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsClientConnecting = false;
                OnPropertyChanged(nameof(IsAnyConnected));
                OnPropertyChanged(nameof(ActiveConnectedStatus));
            }
        }

        public void DisconnectClient()
        {
            try
            {
                _client.Disconnect();
            }
            catch (Exception ex)
            {
                AppendLog($"断开客户端连接异常: {ex.Message}");
            }
            IsClientConnected = false;
            ClientConnectedDevice = "未连接";
            ClientStatusText = "未连接";
            OnPropertyChanged(nameof(IsAnyConnected));
            OnPropertyChanged(nameof(ActiveConnectedStatus));
        }

        // --- File Transfers ---

        public async Task SendDroppedFilesAsync(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;

            // 1. If Client mode and client connected, send via Client
            if (IsClientMode && _client.IsConnected)
            {
                string dest = _client.RemoteHomeDir;
                if (string.IsNullOrWhiteSpace(dest) || dest == "/" || dest == "\\")
                {
                    dest = (_client.RemoteFileSystem == QuickShareDirectory.FILE_SYSTEM_WINDOWS) ? @"C:\Downloads" : "/sdcard/Download";
                }

                AppendLog($"[客户端] 准备发送 {paths.Length} 个文件/文件夹到服务端: {dest}...");
                try
                {
                    await Task.Run(() => _client.SendFilesAsync(paths.ToList(), dest));
                }
                catch (Exception ex)
                {
                    AppendLog($"[客户端] 发送任务异常: {ex.Message}");
                }
                return;
            }

            // 2. If Server connected, send via Server
            if (_server.IsConnected)
            {
                string dest = _server.RemoteHomeDir;
                if (string.IsNullOrWhiteSpace(dest) || dest == "/" || dest == "\\")
                {
                    dest = "/sdcard/Download";
                }

                AppendLog($"[服务端] 准备发送 {paths.Length} 个文件/文件夹到手机目录: {dest}...");
                try
                {
                    await Task.Run(() => _server.SendFilesToRemoteAsync(paths.ToList(), dest));
                }
                catch (Exception ex)
                {
                    AppendLog($"[服务端] 发送任务异常: {ex.Message}");
                }
                return;
            }

            // 3. If Client mode not active but client IS connected, prefer client
            if (_client.IsConnected)
            {
                string dest = _client.RemoteHomeDir;
                if (string.IsNullOrWhiteSpace(dest) || dest == "/" || dest == "\\")
                {
                    dest = (_client.RemoteFileSystem == QuickShareDirectory.FILE_SYSTEM_WINDOWS) ? @"C:\Downloads" : "/sdcard/Download";
                }

                AppendLog($"[客户端] 准备发送 {paths.Length} 个文件/文件夹到服务端: {dest}...");
                try
                {
                    await Task.Run(() => _client.SendFilesAsync(paths.ToList(), dest));
                }
                catch (Exception ex)
                {
                    AppendLog($"[客户端] 发送任务异常: {ex.Message}");
                }
                return;
            }

            // Not connected
            System.Windows.MessageBox.Show(
                "未连接到任何设备！\n\n- 服务端模式：请点击左侧「启动服务」，并在手机/对方客户端上输入本机 IP 进行连接。\n- 客户端模式：请切换至「客户端模式」，输入对方设备显示的 IP 与端口并点击「连接服务端」。",
                "请先连接设备",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }

        public async Task SelectAndSendFilesAsync()
        {
            if (!_server.IsConnected && !_client.IsConnected)
            {
                System.Windows.MessageBox.Show(
                    "未连接到任何设备！\n\n- 服务端模式：请点击左侧「启动服务」，并在手机/对方客户端上输入本机 IP 进行连接。\n- 客户端模式：请切换至「客户端模式」，输入对方设备显示的 IP 与端口并点击「连接服务端」。",
                    "请先连接设备",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            using (var dialog = new System.Windows.Forms.OpenFileDialog())
            {
                dialog.Multiselect = true;
                dialog.Title = "选择要发送的文件";
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    await SendDroppedFilesAsync(dialog.FileNames);
                }
            }
        }

        public async Task SelectAndSendFolderAsync()
        {
            if (!_server.IsConnected && !_client.IsConnected)
            {
                System.Windows.MessageBox.Show(
                    "未连接到任何设备！\n\n- 服务端模式：请点击左侧「启动服务」，并在手机/对方客户端上输入本机 IP 进行连接。\n- 客户端模式：请切换至「客户端模式」，输入对方设备显示的 IP 与端口并点击「连接服务端」。",
                    "请先连接设备",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择要发送的文件夹";
                dialog.UseDescriptionForTitle = true;
                if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    await SendDroppedFilesAsync(new string[] { dialog.SelectedPath });
                }
            }
        }

        // --- Common Helpers ---

        private void CopyIpToClipboard()
        {
            try
            {
                if (!string.IsNullOrEmpty(PrimaryLanIp))
                {
                    System.Windows.Clipboard.SetText(PrimaryLanIp);
                    AppendLog($"已复制本机 IP 地址 ({PrimaryLanIp}) 到剪贴板。");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"复制 IP 到剪贴板失败: {ex.Message}");
            }
        }

        private void RefreshNetworkInfo()
        {
            NetworkInterfaces.Clear();
            var list = _networkService.GetAvailableInterfaces();
            foreach (var ni in list)
            {
                NetworkInterfaces.Add(ni);
            }

            var primary = _networkService.GetPrimaryLanInterface();
            PrimaryLanIp = primary.IpAddress;
            PrimaryNetworkType = primary.InterfaceType;
            PrimaryAdapterName = primary.Name;

            AppendLog($"已刷新网络状态: 本机 LAN IP = {PrimaryLanIp} ({PrimaryNetworkType})");
        }

        private void BrowseSaveDir()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择接收文件的保存目录";
                if (Directory.Exists(SelectedSaveDir))
                {
                    dialog.SelectedPath = SelectedSaveDir;
                }
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    SelectedSaveDir = dialog.SelectedPath;
                }
            }
        }

        private void OpenSaveDir()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SelectedSaveDir))
                {
                    SelectedSaveDir = AppConfig.GetDefaultSaveDirectory();
                }

                if (!Directory.Exists(SelectedSaveDir))
                {
                    Directory.CreateDirectory(SelectedSaveDir);
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = SelectedSaveDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"无法打开目录: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearLogs()
        {
            LogText = string.Empty;
        }

        private void AppendLog(string message)
        {
            SafeDispatch(() =>
            {
                LogText += $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n";
            });
        }

        private void SafeDispatch(Action action)
        {
            try
            {
                if (System.Windows.Application.Current?.Dispatcher != null)
                {
                    if (System.Windows.Application.Current.Dispatcher.CheckAccess())
                    {
                        action();
                    }
                    else
                    {
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
                    }
                }
            }
            catch { }
        }

        private void SetAutoStartRegistry(bool enable)
        {
            try
            {
                const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKeyPath, true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            string appPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                            if (!string.IsNullOrEmpty(appPath))
                            {
                                key.SetValue("QuickShareServer", $"\"{appPath}\" --minimized");
                            }
                        }
                        else
                        {
                            key.DeleteValue("QuickShareServer", false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"设置开机自启失败: {ex.Message}");
            }
        }

        private string FormatSize(long bytes)
        {
            double size = bytes;
            if (size < 1024) return $"{size:F0} B";
            size /= 1024.0;
            if (size < 1024) return $"{size:F1} KB";
            size /= 1024.0;
            if (size < 1024) return $"{size:F1} MB";
            size /= 1024.0;
            return $"{size:F1} GB";
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
