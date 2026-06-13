using System.Globalization;
using System.Windows.Data;

namespace EduStream.Server.Converters;

public sealed class InverseBooleanConverter : IValueConverter
{
    public static InverseBooleanConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool boolean && !boolean;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool boolean && !boolean;
}
