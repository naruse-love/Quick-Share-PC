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
        private AppConfig _config;

        private bool _isServerRunning;
        private string _serverStatusText = "已停止";
        private string _logText = "";
        private string _selectedSaveDir = "";
        private int _port = 5740;
        private bool _autoStart;
        private string _connectedDevice = "未连接";
        private string _primaryLanIp = "127.0.0.1";
        private string _primaryNetworkType = "局域网";
        private string _primaryAdapterName = "本地网络";

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<NetworkInterfaceInfo> NetworkInterfaces { get; } = new();
        public ObservableCollection<TransferTask> TransferTasks { get; } = new();
        public ObservableCollection<TransferHistory> TransferHistories { get; } = new();

        // Commands
        public RelayCommand ToggleServerCommand { get; }
        public RelayCommand BrowseSaveDirCommand { get; }
        public RelayCommand RefreshInterfacesCommand { get; }
        public RelayCommand CopyIpCommand { get; }
        public RelayCommand OpenSaveDirCommand { get; }
        public RelayCommand ClearLogsCommand { get; }
        public RelayCommand SelectAndSendFilesCommand { get; }

        public MainViewModel(QuickShareServer server, ConfigService configService, NetworkService networkService)
        {
            _server = server;
            _configService = configService;
            _networkService = networkService;

            // Load Config
            _config = _configService.LoadConfig();
            SelectedSaveDir = _config.SaveDirectory;
            _server.SaveDirectory = SelectedSaveDir;
            Port = _config.Port;
            AutoStart = _config.AutoStart;

            // Initialize commands
            ToggleServerCommand = new RelayCommand(ToggleServer);
            BrowseSaveDirCommand = new RelayCommand(BrowseSaveDir);
            RefreshInterfacesCommand = new RelayCommand(RefreshNetworkInfo);
            CopyIpCommand = new RelayCommand(CopyIpToClipboard);
            OpenSaveDirCommand = new RelayCommand(OpenSaveDir);
            ClearLogsCommand = new RelayCommand(ClearLogs);
            SelectAndSendFilesCommand = new RelayCommand(async () => await SelectAndSendFilesAsync());

            // Register server events with safe dispatcher invocations
            _server.OnStatusChanged += status =>
            {
                SafeDispatch(() => ServerStatusText = status);
            };

            _server.OnDeviceConnected += ip =>
            {
                SafeDispatch(() => ConnectedDevice = ip);
            };

            _server.OnDeviceDisconnected += () =>
            {
                SafeDispatch(() => ConnectedDevice = "未连接");
            };

            _server.OnLogMessage += msg => AppendLog(msg);

            _server.OnTransferStarted += task =>
            {
                SafeDispatch(() => TransferTasks.Insert(0, task));
            };

            _server.OnTransferProgress += task =>
            {
                // UI automatically updates because TransferTask implements INotifyPropertyChanged
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

            // Load network information
            RefreshNetworkInfo();
        }

        public bool IsServerRunning
        {
            get => _isServerRunning;
            set
            {
                _isServerRunning = value;
                OnPropertyChanged();
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
                        ? $"\n\n原因: 端口 {Port} 被 Windows 保留(例如 Hyper-V / WSL 端口排除范围)或权限不足。\n建议: 请在左侧将端口修改为其他端口(例如 5840、57400 等)后再尝试启动。"
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

        public async Task SendDroppedFilesAsync(string[] paths)
        {
            if (!_server.IsConnected)
            {
                System.Windows.MessageBox.Show("请先连接手机后再发送文件！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string dest = _server.RemoteHomeDir;
            if (string.IsNullOrWhiteSpace(dest) || dest == "/" || dest == "\\")
            {
                dest = "/sdcard/Download";
            }

            AppendLog($"准备发送 {paths.Length} 个文件/文件夹到手机目录: {dest}...");
            try
            {
                await Task.Run(() => _server.SendFilesToRemoteAsync(paths.ToList(), dest));
            }
            catch (Exception ex)
            {
                AppendLog($"发送任务异常: {ex.Message}");
            }
        }

        public async Task SelectAndSendFilesAsync()
        {
            if (!_server.IsConnected)
            {
                System.Windows.MessageBox.Show("请先连接手机后再发送文件！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using (var dialog = new System.Windows.Forms.OpenFileDialog())
            {
                dialog.Multiselect = true;
                dialog.Title = "选择要发送到手机的文件";
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    await SendDroppedFilesAsync(dialog.FileNames);
                }
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
