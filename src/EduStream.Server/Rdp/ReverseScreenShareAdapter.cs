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

    public Task ActivateAdapterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isAdapterActive = true;
        AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs { IsActive = true, SessionState = _reverseSessionManager.CurrentState });
        return Task.CompletedTask;
    }

    public Task DeactivateAdapterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isAdapterActive = false;
        AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs { IsActive = false, SessionState = _reverseSessionManager.CurrentState });
        return Task.CompletedTask;
    }

    public async Task<Guid> StartReverseSharingAsync(Guid sessionId, string studentId, CancellationToken cancellationToken = default) => await _reverseSessionManager.StartReverseSharingAsync(sessionId, studentId, cancellationToken);
    public async Task<ReverseInvitationPacket> CreateProfessorInvitationAsync(Guid s, Guid sh, string p, Guid c, string pw, DateTimeOffset e, CancellationToken ct = default) => await _reverseSessionManager.CreateProfessorInvitationAsync(s, sh, p, c, pw, e, ct);
    public async Task ConnectAsync(CancellationToken cancellationToken = default) => await _reverseSessionManager.ConnectAsync(cancellationToken);
    public async Task OnConnectedAsync(CancellationToken cancellationToken = default) => await _reverseSessionManager.OnConnectedAsync(cancellationToken);
    public async Task OnConnectionFailedAsync(CancellationToken cancellationToken = default) => await _reverseSessionManager.OnConnectionFailedAsync(cancellationToken);
    public async Task OnDisconnectedAsync(CancellationToken cancellationToken = default) => await _reverseSessionManager.OnDisconnectedAsync(cancellationToken);
    public async Task StopReverseSharingAsync(CancellationToken cancellationToken = default) => await _reverseSessionManager.StopReverseSharingAsync(cancellationToken);

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

        // [핵심 변경점] 꼼수 삭제. 실제 디스플레이 핸들러가 성공했을 때만 이벤트 발생 (2절 완벽 대응)
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
        if (frameData != null && frameData.Length >= 26 && frameData[0] == 0x42 && frameData[1] == 0x4D) return (BitConverter.ToInt32(frameData, 18), Math.Abs(BitConverter.ToInt32(frameData, 22)));
        if (frameData != null && frameData.Length >= 12 && frameData[0] == 0x57 && frameData[1] == 0x44 && frameData[2] == 0x53) return (BitConverter.ToInt32(frameData, 4), BitConverter.ToInt32(frameData, 8));
        return (1920, 1080);
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
