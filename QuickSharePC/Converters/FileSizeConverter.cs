using System;
using System.Globalization;
using System.Windows.Data;

namespace QuickShare.PC.Converters
{
    public class FileSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long bytes)
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
            return "0 B";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
