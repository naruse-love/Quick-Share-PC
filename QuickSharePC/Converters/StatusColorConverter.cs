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
                if (status.Contains("运行") || status.Contains("已启动") || status.Contains("连接") || status.Equals("完成"))
                {
                    return new SolidColorBrush(Color.FromRgb(46, 125, 50)); // Green
                }
                if (status.Contains("停止") || status.Equals("失败"))
                {
                    return new SolidColorBrush(Color.FromRgb(198, 40, 40)); // Red
                }
                if (status.Contains("监听") || status.Contains("传输") || status.Contains("中"))
                {
                    return new SolidColorBrush(Color.FromRgb(21, 101, 192)); // Blue
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
