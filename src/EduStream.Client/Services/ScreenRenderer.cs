using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Utils;

namespace EduStream.Client.Services;

/// <summary>
/// 실제 이미지 렌더링 구현 전까지 화면 패킷 메타데이터를 읽어 UI 상태 문자열을 생성합니다.
/// </summary>
public sealed class ScreenRenderer
{
    public string Render(ScreenPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ScreenTransferUtility.ValidatePacketMetadata(packet);

        if (packet.Encoding == ScreenEncodings.Png)
        {
            return $"프레임 #{packet.FrameIndex} 수신 완료 ({packet.Width}x{packet.Height}, PNG, {packet.ContentLength} bytes, {packet.CapturedAt:HH:mm:ss})";
        }

        return $"프레임 #{packet.FrameIndex} 수신 완료 ({packet.Encoding}, {packet.ContentLength} bytes, {packet.CapturedAt:HH:mm:ss})";
    }
}
