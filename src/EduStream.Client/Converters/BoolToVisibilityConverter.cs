using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EduStream.Client.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static BoolToVisibilityConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is bool boolean && boolean;
        if (parameter is string invert && invert == "Invert")
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility visibility && visibility == Visibility.Visible;
}
