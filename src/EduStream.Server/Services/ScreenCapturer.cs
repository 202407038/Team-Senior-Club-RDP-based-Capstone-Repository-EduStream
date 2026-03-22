using EduStream.Core.Models;

namespace EduStream.Server.Services;

/// <summary>
/// 실제 화면 캡처 라이브러리 연동 전까지 프레임 메타데이터를 생성합니다.
/// </summary>
public sealed class ScreenCapturer
{
    private int _frameIndex;

    public ScreenPacket CapturePreviewFrame()
    {
        _frameIndex++;
        return new ScreenPacket
        {
            FrameIndex = _frameIndex,
            FrameDescription = $"미리보기 프레임 {_frameIndex}",
            Content = []
        };
    }
}
