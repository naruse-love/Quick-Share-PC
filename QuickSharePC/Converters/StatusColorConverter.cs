using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace QuickShare.PC.Converters
{
    public class StatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                if (status.Contains("未连接") || status.Contains("已停止"))
                {
                    return new SolidColorBrush(Color.FromRgb(148, 163, 184)); // Slate Gray (#94A3B8)
                }
                if (status.Contains("失败") || status.Contains("错误") || status.Contains("停止"))
                {
                    return new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red (#EF4444)
                }
                if (status.Contains("正在连接") || status.Contains("监听") || status.Contains("传输") || status.Contains("中"))
                {
                    return new SolidColorBrush(Color.FromRgb(37, 99, 235)); // Blue (#2563EB)
                }
                if (status.Contains("运行") || status.Contains("已启动") || status.Contains("已连接") || status.Equals("完成"))
                {
                    return new SolidColorBrush(Color.FromRgb(22, 163, 74)); // Green (#16A34A)
                }
            }
            if (value is bool isRunning)
            {
                return isRunning ? new SolidColorBrush(Color.FromRgb(46, 125, 50)) : new SolidColorBrush(Color.FromRgb(198, 40, 40));
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
