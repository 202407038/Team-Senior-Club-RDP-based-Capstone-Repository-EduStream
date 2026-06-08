using System.Globalization;
using System.Windows.Data;

namespace EduStream.Client;

/// <summary>
/// bool 값을 반전시켜 UI 바인딩에 사용합니다.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public static InverseBooleanConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool boolean && !boolean;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool boolean && !boolean;
    }
}
