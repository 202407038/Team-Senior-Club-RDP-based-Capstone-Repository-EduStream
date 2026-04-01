using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Utils;

namespace EduStream.Server.Services;

/// <summary>
/// 실제 화면 캡처 라이브러리를 붙이기 전까지 서버가 최소한의 프레임 데이터를 만들 수 있도록 돕습니다.
/// 가능하면 현재 데스크톱을 PNG로 캡처하고, 실패하면 플레이스홀더 프레임을 생성합니다.
/// </summary>
public sealed class ScreenCapturer
{
    private int _frameIndex;

    public ScreenPacket CapturePreviewFrame()
    {
        _frameIndex++;

        try
        {
            return CaptureDesktopFrame(_frameIndex);
        }
        catch
        {
            return CreatePlaceholderFrame(_frameIndex);
        }
    }

    private static ScreenPacket CaptureDesktopFrame(int frameIndex)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);

        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        }

        return CreatePngPacket(
            frameIndex,
            $"Desktop preview frame {frameIndex}",
            bitmap);
    }

    private static ScreenPacket CreatePlaceholderFrame(int frameIndex)
    {
        const int width = 1280;
        const int height = 720;

        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(35, 44, 56));

            using var titleBrush = new SolidBrush(Color.White);
            using var subtitleBrush = new SolidBrush(Color.FromArgb(200, 215, 226));
            using var titleFont = new Font("Segoe UI", 26, FontStyle.Bold);
            using var subtitleFont = new Font("Segoe UI", 14, FontStyle.Regular);

            graphics.DrawString("EduStream Preview Frame", titleFont, titleBrush, new PointF(48, 52));
            graphics.DrawString(
                "실제 데스크톱 캡처에 실패하여 임시 플레이스홀더 프레임을 표시합니다.",
                subtitleFont,
                subtitleBrush,
                new PointF(52, 112));
            graphics.DrawString(
                $"Frame #{frameIndex}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                subtitleFont,
                subtitleBrush,
                new PointF(52, 148));
        }

        return CreatePngPacket(
            frameIndex,
            $"Placeholder preview frame {frameIndex}",
            bitmap);
    }

    private static ScreenPacket CreatePngPacket(int frameIndex, string description, Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);

        var content = stream.ToArray();
        var packet = new ScreenPacket
        {
            FrameIndex = frameIndex,
            FrameDescription = description,
            CapturedAt = DateTimeOffset.UtcNow,
            Width = bitmap.Width,
            Height = bitmap.Height,
            Encoding = ScreenEncodings.Png,
            Content = content,
            DataLength = content.Length
        };

        ScreenTransferUtility.ValidatePacketMetadata(packet);
        return packet;
    }
}
