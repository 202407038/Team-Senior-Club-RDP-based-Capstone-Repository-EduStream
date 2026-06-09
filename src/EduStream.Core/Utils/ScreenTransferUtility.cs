using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.Core.Utils;

/// <summary>
/// 화면 프레임 전송 시 서버와 클라이언트가 공통으로 따를 최소 검증 규칙을 제공합니다.
/// </summary>
public static class ScreenTransferUtility
{
    public static void ValidateCaptureSettings(ScreenCaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.TargetFrameIntervalMilliseconds < ScreenTransferRules.MinimumFrameIntervalMilliseconds ||
            settings.TargetFrameIntervalMilliseconds > ScreenTransferRules.MaximumFrameIntervalMilliseconds)
        {
            throw new InvalidOperationException(ErrorCodes.InvalidFrameInterval);
        }

        if (settings.Encoding != ScreenEncodings.Png)
        {
            throw new InvalidOperationException(ErrorCodes.InvalidScreenEncoding);
        }
    }

    public static void ValidatePacketMetadata(ScreenPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.FrameIndex <= 0)
        {
            throw new InvalidOperationException(ErrorCodes.InvalidFrameDimensions);
        }

        if (packet.Width <= 0 || packet.Height <= 0)
        {
            throw new InvalidOperationException(ErrorCodes.InvalidFrameDimensions);
        }

        if (string.IsNullOrWhiteSpace(packet.Encoding))
        {
            throw new InvalidOperationException(ErrorCodes.InvalidScreenEncoding);
        }

        if (packet.Encoding != ScreenEncodings.Png && packet.Encoding != ScreenEncodings.Raw)
        {
            throw new InvalidOperationException(ErrorCodes.InvalidScreenEncoding);
        }

        if (packet.Content.Length == 0)
        {
            throw new InvalidOperationException(ErrorCodes.EmptyScreenPayload);
        }
    }
}
