using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;

namespace QuickShare.PC.Services
{
    public class TrayService : IDisposable
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly NotifyIcon _notifyIcon;
        private readonly Window _mainWindow;
        private readonly Action _onExit;
        private readonly Action _onToggleServer;
        private readonly Func<bool> _isServerRunning;

        public TrayService(Window mainWindow, Action onToggleServer, Func<bool> isServerRunning, Action onExit)
        {
            _mainWindow = mainWindow;
            _onToggleServer = onToggleServer;
            _isServerRunning = isServerRunning;
            _onExit = onExit;

            _notifyIcon = new NotifyIcon
            {
                Text = "Quick Share 服务端",
                Visible = true
            };

            _notifyIcon.Icon = CreateProgrammaticIcon();
            _notifyIcon.DoubleClick += (s, e) => ShowWindow();

            CreateContextMenu();
        }

        private Icon CreateProgrammaticIcon()
        {
            try
            {
                using (var bitmap = new Bitmap(32, 32))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    // Draw a nice blue circular background
                    using (var brush = new SolidBrush(Color.FromArgb(0, 120, 215)))
                    {
                        g.FillEllipse(brush, 2, 2, 28, 28);
                    }

                    // Draw a white 'Q' in the center
                    using (var font = new Font("Arial", 16, System.Drawing.FontStyle.Bold))
                    using (var brush = new SolidBrush(Color.White))
                    {
                        var sf = new StringFormat
                        {
                            Alignment = StringAlignment.Center,
                            LineAlignment = StringAlignment.Center
                        };
                        g.DrawString("Q", font, brush, new RectangleF(0, 0, 32, 32), sf);
                    }

                    IntPtr hIcon = bitmap.GetHicon();
                    try
                    {
                        using (var tempIcon = Icon.FromHandle(hIcon))
                        {
                            return (Icon)tempIcon.Clone();
                        }
                    }
                    finally
                    {
                        DestroyIcon(hIcon);
                    }
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        private void CreateContextMenu()
        {
            var menu = new ContextMenuStrip();

            var showItem = new ToolStripMenuItem("显示主界面", null, (s, e) => ShowWindow());
            var toggleItem = new ToolStripMenuItem("启动/停止 服务", null, (s, e) => _onToggleServer());
            
            // Add a separator
            menu.Items.Add(showItem);
            menu.Items.Add(toggleItem);
            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("退出程序", null, (s, e) => _onExit());
            menu.Items.Add(exitItem);

            // Dynamically update toggle item text on opening
            menu.Opening += (s, e) =>
            {
                toggleItem.Text = _isServerRunning() ? "停止服务" : "启动服务";
            };

            _notifyIcon.ContextMenuStrip = menu;
        }

        public void ShowWindow()
        {
            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
            _mainWindow.Activate();
        }

        public void HideWindow()
        {
            _mainWindow.Hide();
        }

        public void ShowNotification(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
        {
            _notifyIcon.ShowBalloonTip(3000, title, text, icon);
        }

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}
