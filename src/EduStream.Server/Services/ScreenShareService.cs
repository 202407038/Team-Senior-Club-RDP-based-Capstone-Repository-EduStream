using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Utils;

namespace EduStream.Server.Services;

/// <summary>
/// 서버 화면 캡처와 브로드캐스트 진입점을 한 곳으로 모으는 서비스입니다.
/// Day 5 전까지는 단일 프레임 미리보기 전송을 기준으로 두고, 이후 반복 송신 루프로 확장합니다.
/// </summary>
public sealed class ScreenShareService
{
    private readonly SessionManager _sessionManager;
    private readonly ILogSink _logSink;

    public ScreenShareService(SessionManager sessionManager, ILogSink logSink, ScreenCaptureSettings? settings = null)
    {
        _sessionManager = sessionManager;
        _logSink = logSink;
        Settings = settings ?? new ScreenCaptureSettings();
        ScreenTransferUtility.ValidateCaptureSettings(Settings);
    }

    public ScreenCaptureSettings Settings { get; private set; }

    public void UpdateSettings(ScreenCaptureSettings settings)
    {
        ScreenTransferUtility.ValidateCaptureSettings(settings);
        Settings = settings;
        _logSink.Write($"화면 송신 설정 갱신: {settings.CaptureSourceName}, {settings.Encoding}, {settings.TargetFrameIntervalMilliseconds}ms");
    }

    public async Task<ScreenPacket> CaptureAndBroadcastPreviewAsync()
    {
        var capturer = new ScreenCapturer(Settings);
        var frame = capturer.CapturePreviewFrame();
        frame.DataLength = frame.Content.Length;

        await _sessionManager.BroadcastPacketAsync(frame);
        _logSink.Write($"화면 프레임 전송: #{frame.FrameIndex}, {frame.Width}x{frame.Height}, {frame.Encoding}");

        return frame;
    }

    public string BuildLatestStatus(ScreenPacket frame)
    {
        return $"{frame.FrameDescription} 전송 완료 ({frame.Width}x{frame.Height}, {frame.ContentLength} bytes, {frame.CapturedAt:HH:mm:ss}, {Settings.TargetFrameIntervalMilliseconds}ms)";
    }
}
