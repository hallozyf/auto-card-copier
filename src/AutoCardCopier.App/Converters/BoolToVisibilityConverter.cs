using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AutoCardCopier.App.Converters
{
    /// <summary>布尔值到可见性的转换器：true 显示，false 折叠。</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = value is bool && (bool)value;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility && (Visibility)value == Visibility.Visible;
        }
    }
}
