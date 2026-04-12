using EduStream.Core.Protocols;

namespace EduStream.Core.Models;

/// <summary>
/// 서버 화면 캡처 시작 전에 확인할 기본 설정입니다.
/// Day 1 단계에서는 인코딩과 목표 프레임 간격을 공통 기준으로 고정하는 용도로 사용합니다.
/// </summary>
public sealed class ScreenCaptureSettings
{
    public int TargetFrameIntervalMilliseconds { get; init; } = ScreenTransferRules.DefaultFrameIntervalMilliseconds;

    public string Encoding { get; init; } = ScreenTransferRules.DefaultEncoding;

    public string CaptureSourceName { get; init; } = "Primary screen";
}
