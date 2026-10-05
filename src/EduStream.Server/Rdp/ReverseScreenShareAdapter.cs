using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

public sealed class ReverseScreenShareAdapter : IReverseScreenShareAdapter
{
    private readonly IReverseSessionManager _reverseSessionManager;
    private readonly List<Func<byte[], Task>> _frameReceivers = new();
    private readonly List<Func<byte[], int, int, Task>> _displayHandlers = new();
    private readonly object _pipelineLock = new();

    private bool _isAdapterActive = false;
    private int _currentFrameWidth = 0;
    private int _currentFrameHeight = 0;
    private long _totalFramesProcessed = 0;

    private ReverseSessionState _lastNotifiedState = ReverseSessionState.Inactive;

    public bool IsAdapterActive => _isAdapterActive;
    public ReverseSessionState CurrentState => _reverseSessionManager.CurrentState;
    public int CurrentFrameWidth => _currentFrameWidth;
    public int CurrentFrameHeight => _currentFrameHeight;
    public long TotalFramesProcessed => _totalFramesProcessed;

    public event EventHandler<AdapterStateChangedEventArgs>? AdapterStateChanged;
    public event EventHandler<FrameProcessedEventArgs>? FrameProcessed;
    public event EventHandler<FrameDisplayedEventArgs>? FrameDisplayed;

    public ReverseScreenShareAdapter(IReverseSessionManager reverseSessionManager)
    {
        _reverseSessionManager = reverseSessionManager ?? throw new ArgumentNullException(nameof(reverseSessionManager));
        _reverseSessionManager.FrameReceived += OnFrameReceived;
    }

    // 🎯 [피드백 7번 반영] 알림 강제 발송 파라미터(forceNotify) 추가
    // 단순히 _isAdapterActive 여부에 의존하면 비활성화 순간을 놓치므로(false로 바뀐 뒤 호출되므로),
    // 명시적으로 "지금 알림을 쏴라"라는 신호를 줄 수 있도록 개선했습니다.
    private void NotifyStateChangedIfNeeded(bool forceNotify = false)
    {
        var currentState = _reverseSessionManager.CurrentState;
        if (forceNotify || _lastNotifiedState != currentState || _isAdapterActive)
        {
            _lastNotifiedState = currentState;
            AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs
            {
                IsActive = _isAdapterActive,
                SessionState = currentState
            });
        }
    }

    public Task ActivateAdapterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isAdapterActive = true;
        NotifyStateChangedIfNeeded(forceNotify: true); // 활성화 알림 강제 발송
        return Task.CompletedTask;
    }

    public Task DeactivateAdapterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 🎯 [피드백 7번 반영] 상태 변경과 알림 발송의 순서 무결성 확보
        // 1. 먼저 비활성 상태로 변경
        _isAdapterActive = false;

        // 2. 바뀐 상태(Inactive)를 강제로 즉시 알림 발송 (if문 우회)
        NotifyStateChangedIfNeeded(forceNotify: true);

        return Task.CompletedTask;
    }

    public async Task<Guid> StartReverseSharingAsync(Guid sessionId, string studentId, CancellationToken cancellationToken = default)
    {
        var result = await _reverseSessionManager.StartReverseSharingAsync(sessionId, studentId, cancellationToken);
        NotifyStateChangedIfNeeded();
        return result;
    }

    public async Task<ReverseInvitationPacket> CreateProfessorInvitationAsync(Guid s, Guid sh, string p, Guid c, string pw, DateTimeOffset e, CancellationToken ct = default)
    {
        var result = await _reverseSessionManager.CreateProfessorInvitationAsync(s, sh, p, c, pw, e, ct);
        NotifyStateChangedIfNeeded();
        return result;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _reverseSessionManager.ConnectAsync(cancellationToken);
        NotifyStateChangedIfNeeded();
    }

    public async Task OnConnectedAsync(CancellationToken cancellationToken = default)
    {
        await _reverseSessionManager.OnConnectedAsync(cancellationToken);
        NotifyStateChangedIfNeeded();
    }

    public async Task OnConnectionFailedAsync(CancellationToken cancellationToken = default)
    {
        await _reverseSessionManager.OnConnectionFailedAsync(cancellationToken);
        NotifyStateChangedIfNeeded();
    }

    public async Task OnDisconnectedAsync(CancellationToken cancellationToken = default)
    {
        await _reverseSessionManager.OnDisconnectedAsync(cancellationToken);
        NotifyStateChangedIfNeeded();
    }

    public async Task StopReverseSharingAsync(CancellationToken cancellationToken = default)
    {
        await _reverseSessionManager.StopReverseSharingAsync(cancellationToken);
        NotifyStateChangedIfNeeded();
    }

    public void AddFrameReceiver(Func<byte[], Task> receiver) { lock (_pipelineLock) _frameReceivers.Add(receiver); }
    public void AddDisplayHandler(Func<byte[], int, int, Task> displayHandler) { lock (_pipelineLock) _displayHandlers.Add(displayHandler); }
    public void ClearFrameReceivers() { lock (_pipelineLock) _frameReceivers.Clear(); }
    public void ClearDisplayHandlers() { lock (_pipelineLock) _displayHandlers.Clear(); }

    private async void OnFrameReceived(object? sender, FrameReceivedEventArgs e)
    {
        if (!_isAdapterActive || e.FrameData == null || e.FrameData.Length == 0) return;

        Interlocked.Increment(ref _totalFramesProcessed);

        var width = EstimateFrameWidth(e.FrameData);
        var height = EstimateFrameHeight(e.FrameData);
        if (width <= 0 || height <= 0) return;

        _currentFrameWidth = width;
        _currentFrameHeight = height;

        FrameProcessed?.Invoke(this, new FrameProcessedEventArgs { FrameData = e.FrameData, Timestamp = e.Timestamp });

        Func<byte[], Task>[] receiversCopy;
        Func<byte[], int, int, Task>[] displayHandlersCopy;

        lock (_pipelineLock)
        {
            receiversCopy = _frameReceivers.ToArray();
            displayHandlersCopy = _displayHandlers.ToArray();
        }

        foreach (var receiver in receiversCopy)
        {
            if (!_isAdapterActive) return;
            try { await receiver(e.FrameData); } catch { }
        }

        int successDisplayCount = 0;
        foreach (var displayHandler in displayHandlersCopy)
        {
            if (!_isAdapterActive) return;
            try
            {
                await displayHandler(e.FrameData, width, height);
                successDisplayCount++;
            }
            catch { }
        }

        if (!_isAdapterActive) return;

        if (successDisplayCount > 0)
        {
            FrameDisplayed?.Invoke(this, new FrameDisplayedEventArgs
            {
                FrameData = e.FrameData, Width = width, Height = height, Timestamp = e.Timestamp
            });
        }
    }

    public static int EstimateFrameWidth(byte[] frameData) => ParseFrameDimensions(frameData).width;
    public static int EstimateFrameHeight(byte[] frameData) => ParseFrameDimensions(frameData).height;

    private static (int width, int height) ParseFrameDimensions(byte[] frameData)
    {
        if (frameData == null || frameData.Length < 12) return (1920, 1080);

        int w = 1920, h = 1080;

        // 1. BMP 헤더
        if (frameData.Length >= 26 && frameData[0] == 0x42 && frameData[1] == 0x4D)
        {
            w = BitConverter.ToInt32(frameData, 18);
            h = Math.Abs(BitConverter.ToInt32(frameData, 22));
        }
        // 2. WDS 헤더
        else if (frameData[0] == 0x57 && frameData[1] == 0x44 && frameData[2] == 0x53)
        {
            w = BitConverter.ToInt32(frameData, 4);
            h = BitConverter.ToInt32(frameData, 8);
        }
        // 3. PNG 헤더
        else if (frameData.Length >= 24 && frameData[0] == 0x89 && frameData[1] == 0x50 && frameData[2] == 0x4E && frameData[3] == 0x47)
        {
            byte[] wBytes = { frameData[19], frameData[18], frameData[17], frameData[16] };
            byte[] hBytes = { frameData[23], frameData[22], frameData[21], frameData[20] };
            w = BitConverter.ToInt32(wBytes, 0);
            h = BitConverter.ToInt32(hBytes, 0);
        }

        if (w <= 0) w = 1920;
        if (h <= 0) h = 1080;

        return (w, h);
    }
}

public sealed class AdapterStateChangedEventArgs : EventArgs
{
    public bool IsActive { get; init; }
    public ReverseSessionState SessionState { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class FrameProcessedEventArgs : EventArgs
{
    public byte[] FrameData { get; init; } = Array.Empty<byte>();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}