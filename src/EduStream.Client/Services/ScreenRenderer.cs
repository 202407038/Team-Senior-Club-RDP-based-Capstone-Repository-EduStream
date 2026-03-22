using EduStream.Core.Models;

namespace EduStream.Client.Services;

/// <summary>
/// 추후 실제 이미지 렌더링 로직이 들어갈 위치입니다.
/// 현재는 화면 상태 문구를 생성합니다.
/// </summary>
public sealed class ScreenRenderer
{
    public string Render(ScreenPacket packet)
    {
        return $"프레임 #{packet.FrameIndex} 표시 준비 완료";
    }
}
