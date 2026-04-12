namespace EduStream.Core.Protocols;

/// <summary>
/// 화면 프레임 전송 시 서버와 클라이언트가 공통으로 참고하는 기본 규칙입니다.
/// 초기 스프린트에서는 이 값을 기준으로 캡처 간격과 인코딩 규칙을 고정합니다.
/// </summary>
public static class ScreenTransferRules
{
    public const int DefaultFrameIntervalMilliseconds = 1000;
    public const int MinimumFrameIntervalMilliseconds = 100;
    public const int MaximumFrameIntervalMilliseconds = 5000;
    public const string DefaultEncoding = ScreenEncodings.Png;
}
