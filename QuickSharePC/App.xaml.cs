using System;
using System.Linq;
using System.Threading;
using System.Windows;
using QuickShare.PC.Services;
using QuickShare.PC.ViewModels;

namespace QuickShare.PC
{
    public partial class App : Application
    {
        private Mutex? _singleInstanceMutex;
        private QuickShareServer? _server;
        private TrayService? _trayService;
        private MainViewModel? _viewModel;
        private bool _isExiting = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Global unhandled exception handlers to prevent silent crashes
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                var ex = ev.ExceptionObject as Exception;
                MessageBox.Show($"应用程序发生未捕获异常:\n{ex?.Message}\n\n堆栈跟踪:\n{ex?.StackTrace}", "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            DispatcherUnhandledException += (s, ev) =>
            {
                MessageBox.Show($"UI 线程异常:\n{ev.Exception.Message}\n\n堆栈跟踪:\n{ev.Exception.StackTrace}", "程序错误", MessageBoxButton.OK, MessageBoxImage.Error);
                ev.Handled = true;
            };

            // Ensure single instance running
            _singleInstanceMutex = new Mutex(true, "QuickShareServerMutex", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Quick Share 服务端已在运行中！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // We use explicit shutdown so the app can stay alive in tray
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Initialize Services
            _server = new QuickShareServer();
            var configService = new ConfigService();
            var networkService = new NetworkService();

            // Initialize ViewModel
            _viewModel = new MainViewModel(_server, configService, networkService);

            // Create MainWindow
            var mainWindow = new MainWindow
            {
                DataContext = _viewModel
            };
            MainWindow = mainWindow;

            // Setup TrayService
            _trayService = new TrayService(
                mainWindow,
                onToggleServer: () =>
                {
                    _viewModel.ToggleServer();
                },
                isServerRunning: () => _viewModel.IsServerRunning,
                onExit: ExitApp
            );

            // Hook window closing to minimize to tray
            mainWindow.Closing += (s, ev) =>
            {
                if (!_isExiting)
                {
                    ev.Cancel = true;
                    _trayService.HideWindow();
                    _trayService.ShowNotification("已最小化到托盘", "Quick Share 服务端仍在后台运行。");
                }
            };

            // Check launch arguments
            bool startMinimized = e.Args.Contains("--minimized");
            if (startMinimized)
            {
                _trayService.HideWindow();
            }
            else
            {
                _trayService.ShowWindow();
            }

            // Auto-start server if configured
            var config = configService.LoadConfig();
            if (config.AutoStartServer)
            {
                _viewModel.ToggleServer();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            CleanUp();
            base.OnExit(e);
        }

        private void ExitApp()
        {
            if (_isExiting) return;
            _isExiting = true;
            CleanUp();
            Shutdown();
        }

        private void CleanUp()
        {
            try
            {
                _trayService?.Dispose();
                _trayService = null;
            }
            catch { }

            try
            {
                _server?.Stop();
                _server = null;
            }
            catch { }

            if (_singleInstanceMutex != null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch { }
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
        }
    }
}
