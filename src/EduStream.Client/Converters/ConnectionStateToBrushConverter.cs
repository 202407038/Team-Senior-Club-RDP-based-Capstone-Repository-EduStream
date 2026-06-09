using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace EduStream.Client.Converters;

public sealed class ConnectionStateToBrushConverter : IValueConverter
{
    public static ConnectionStateToBrushConverter Instance { get; } = new();

    private static readonly SolidColorBrush Connected = new(Color.FromRgb(3, 218, 198));
    private static readonly SolidColorBrush Error = new(Color.FromRgb(207, 102, 121));
    private static readonly SolidColorBrush Waiting = new(Color.FromRgb(136, 136, 136));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;

        if (text.Contains("연결됨", StringComparison.Ordinal)
            || text.Contains("Open", StringComparison.OrdinalIgnoreCase)
            || text.Contains("active", StringComparison.OrdinalIgnoreCase))
        {
            return Connected;
        }

        if (text.Contains("실패", StringComparison.Ordinal)
            || text.Contains("해제", StringComparison.Ordinal)
            || text.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return Error;
        }

        return Waiting;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
