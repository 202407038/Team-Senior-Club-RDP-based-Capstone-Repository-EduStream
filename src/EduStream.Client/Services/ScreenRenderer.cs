using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.Client.Services;

/// <summary>
/// 화면 패킷을 UI 상태 문자열과 WPF 이미지 소스로 변환합니다.
/// </summary>
public sealed class ScreenRenderer
{
    public string Render(ScreenPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.ContentLength == 0)
        {
            return $"프레임 #{packet.FrameIndex} — 표시할 데이터가 없습니다.";
        }

        if (packet.Encoding == ScreenEncodings.Png)
        {
            return $"프레임 #{packet.FrameIndex} 수신 ({packet.Width}x{packet.Height}, {packet.ContentLength:N0} bytes)";
        }

        return $"프레임 #{packet.FrameIndex} 수신 ({packet.Encoding}, {packet.ContentLength:N0} bytes)";
    }

    public BitmapImage? TryCreateDisplayImage(ScreenPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.ContentLength > 0 && packet.Encoding == ScreenEncodings.Png)
        {
            return LoadPngImage(packet.Content);
        }

        if (packet.ContentLength == 0)
        {
            return CreatePlaceholderImage(
                Math.Max(packet.Width, 960),
                Math.Max(packet.Height, 540),
                packet.FrameDescription,
                packet.FrameIndex);
        }

        return null;
    }

    public BitmapImage CreateDemoFrame(int frameIndex, string description)
    {
        return CreatePlaceholderImage(960, 540, description, frameIndex);
    }

    private static BitmapImage LoadPngImage(byte[] content)
    {
        using var stream = new MemoryStream(content);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapImage CreatePlaceholderImage(int width, int height, string description, int frameIndex)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(35, 44, 56)), null, new Rect(0, 0, width, height));

            var title = new FormattedText(
                "EduStream 강의 화면",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                28,
                Brushes.White,
                VisualTreeHelper.GetDpi(visual).PixelsPerDip);

            var subtitle = new FormattedText(
                string.IsNullOrWhiteSpace(description) ? "샘플 프레임" : description,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                15,
                new SolidColorBrush(Color.FromRgb(200, 215, 226)),
                VisualTreeHelper.GetDpi(visual).PixelsPerDip);

            var meta = new FormattedText(
                $"Frame #{frameIndex}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                13,
                new SolidColorBrush(Color.FromRgb(160, 180, 195)),
                VisualTreeHelper.GetDpi(visual).PixelsPerDip);

            context.DrawText(title, new Point(48, 48));
            context.DrawText(subtitle, new Point(52, 96));
            context.DrawText(meta, new Point(52, 132));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return LoadPngImage(stream.ToArray());
    }
}
