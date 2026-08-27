using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Utils;

namespace EduStream.Server.Services;

/// <summary>
/// 서버 화면 캡처와 전송 루프를 하나의 서비스로 묶습니다.
/// 단발 미리보기 전송과 주기적 자동 송신을 모두 이 서비스에서 관리합니다.
/// </summary>
public sealed class ScreenShareService
{
    private readonly SessionManager _sessionManager;
    private readonly ILogSink _logSink;
    private readonly object _streamLock = new();
    private ScreenCapturer _capturer;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private int _broadcastFrameCount;
    private DateTimeOffset? _lastBroadcastedAt;
    private string _latestStatus = "화면 송신 대기 중";
    private OperationState _state = OperationState.Idle;

    public ScreenShareService(SessionManager sessionManager, ILogSink logSink, ScreenCaptureSettings? settings = null)
    {
        _sessionManager = sessionManager;
        _logSink = logSink;
        Settings = settings ?? new ScreenCaptureSettings();
        ScreenTransferUtility.ValidateCaptureSettings(Settings);
        _capturer = new ScreenCapturer(Settings);
    }

    public ScreenCaptureSettings Settings { get; private set; }

    public bool IsStreaming
    {
        get
        {
            lock (_streamLock)
            {
                return _streamTask is not null && !_streamTask.IsCompleted;
            }
        }
    }

    public int BroadcastFrameCount
    {
        get
        {
            lock (_streamLock)
            {
                return _broadcastFrameCount;
            }
        }
    }

    public DateTimeOffset? LastBroadcastedAt
    {
        get
        {
            lock (_streamLock)
            {
                return _lastBroadcastedAt;
            }
        }
    }

    public string LatestStatus
    {
        get
        {
            lock (_streamLock)
            {
                return _latestStatus;
            }
        }
    }

    public OperationState State
    {
        get
        {
            lock (_streamLock)
            {
                return _state;
            }
        }
    }

    public event Action? StatusChanged;

    public void UpdateSettings(ScreenCaptureSettings settings)
    {
        ScreenTransferUtility.ValidateCaptureSettings(settings);
        Settings = settings;
        _capturer = new ScreenCapturer(settings);

        _logSink.Write($"[Screen] 화면 송신 설정 갱신: {settings.CaptureSourceName}, {settings.Encoding}, {settings.TargetFrameIntervalMilliseconds}ms");
        UpdateStatus($"화면 송신 설정 갱신: {settings.CaptureSourceName}, {settings.TargetFrameIntervalMilliseconds}ms");
    }

    public async Task<ScreenPacket> CaptureAndBroadcastPreviewAsync()
    {
        var frame = _capturer.CapturePreviewFrame();

        if (IsPlaceholderFrame(frame))
        {
            _logSink.Write($"[Screen] 화면 캡처 fallback: #{frame.FrameIndex}, 플레이스홀더 프레임 사용");
            UpdateStatus($"화면 캡처 fallback: 프레임#{frame.FrameIndex} 플레이스홀더 사용");
        }

        await BroadcastFrameAsync(frame);
        return frame;
    }

    public Task StartContinuousBroadcastAsync()
    {
        lock (_streamLock)
        {
            if (_streamTask is not null && !_streamTask.IsCompleted)
            {
                _logSink.Write("[Screen] 화면 자동 송신이 이미 실행 중입니다.");
                return Task.CompletedTask;
            }

            _streamCts = new CancellationTokenSource();
            _streamTask = Task.Run(() => BroadcastLoopAsync(_streamCts.Token));
            _latestStatus = $"화면 자동 송신 시작 ({Settings.TargetFrameIntervalMilliseconds}ms 간격)";
            _state = OperationState.InProgress;
        }

        _logSink.Write($"[Screen] {_latestStatus}");
        StatusChanged?.Invoke();
        return Task.CompletedTask;
    }

    public async Task StopContinuousBroadcastAsync()
    {
        CancellationTokenSource? cts;
        Task? streamTask;

        lock (_streamLock)
        {
            cts = _streamCts;
            streamTask = _streamTask;
        }

        if (cts is null || streamTask is null)
        {
            _logSink.Write("[Screen] 화면 자동 송신 중지 요청: 실행 중인 송신이 없습니다.");
            UpdateStatus("화면 자동 송신이 실행 중이 아닙니다.", OperationState.Idle);
            return;
        }

        _logSink.Write("[Screen] 화면 자동 송신 중지 요청");
        cts.Cancel();

        try
        {
            await streamTask;
        }
        catch (OperationCanceledException)
        {
        }

        _logSink.Write("[Screen] 화면 자동 송신 중지 완료");
        UpdateStatus("화면 자동 송신 중지", OperationState.Stopped);
    }

    public string BuildLatestStatus(ScreenPacket frame)
    {
        return $"{frame.FrameDescription} 전송 완료 ({frame.Width}x{frame.Height}, {frame.ContentLength} bytes, {frame.CapturedAt:HH:mm:ss}, {Settings.TargetFrameIntervalMilliseconds}ms)";
    }

    private async Task BroadcastLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await CaptureAndBroadcastPreviewAsync();
                    // 정상 전송 후 상태를 InProgress로 복구
                    UpdateStatus("화면 프레임 전송 중", OperationState.InProgress);
                }
                catch (Exception ex)
                {
                    // 프레임 단위 실패는 루프를 중단하지 않고 다음 주기로 넘깁니다.
                    _logSink.Write($"[Screen] 화면 프레임 송신 실패(루프 유지): {ex.GetType().Name}: {ex.Message}");
                    UpdateStatus($"화면 프레임 송신 실패(루프 유지): {ex.Message}", OperationState.Failed);
                }

                await Task.Delay(Settings.TargetFrameIntervalMilliseconds, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logSink.Write($"[Screen] 화면 자동 송신 실패: {ex.Message}");
            UpdateStatus($"화면 자동 송신 실패: {ex.Message}", OperationState.Failed);
        }
        finally
        {
            lock (_streamLock)
            {
                _streamCts?.Dispose();
                _streamCts = null;
                _streamTask = null;
            }

            StatusChanged?.Invoke();
        }
    }

    private async Task BroadcastFrameAsync(ScreenPacket frame)
    {
        frame.DataLength = frame.Content.Length;

        try
        {
            await _sessionManager.BroadcastPacketAsync(frame);
            _logSink.Write($"[Screen] 화면 프레임 전송: #{frame.FrameIndex}, {frame.Width}x{frame.Height}, {frame.Encoding}");

            lock (_streamLock)
            {
                _broadcastFrameCount++;
                _lastBroadcastedAt = frame.CapturedAt;
                _latestStatus = BuildLatestStatus(frame);
            }

            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logSink.Write($"[Screen] 화면 프레임 전송 실패: #{frame.FrameIndex}, {ex.GetType().Name}: {ex.Message}");
            UpdateStatus($"화면 프레임 전송 실패: #{frame.FrameIndex}, {ex.Message}");
        }
    }

    private static bool IsPlaceholderFrame(ScreenPacket frame)
    {
        return frame.FrameDescription.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateStatus(string status, OperationState? state = null)
    {
        lock (_streamLock)
        {
            _latestStatus = status;
            if (state.HasValue)
            {
                _state = state.Value;
            }
        }

        StatusChanged?.Invoke();
    }
}
