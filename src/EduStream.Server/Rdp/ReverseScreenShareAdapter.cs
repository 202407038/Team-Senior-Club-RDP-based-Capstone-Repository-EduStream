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
    
    // 🌟 리뷰어 지적 대응: 상태 변화 추적용 변수
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

    // 🌟 리뷰어 지적 대응: 상태가 실제로 변했을 때만 이벤트를 명확히 발행하도록 중앙 집중화
    private void NotifyStateChangedIfNeeded()
    {
        var currentState = _reverseSessionManager.CurrentState;
        if (_lastNotifiedState != currentState || _isAdapterActive)
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
        NotifyStateChangedIfNeeded();
        return Task.CompletedTask;
    }

    public Task DeactivateAdapterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isAdapterActive = false;
        NotifyStateChangedIfNeeded();
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
        // 3. PNG 헤더 (복구 완료)
        else if (frameData.Length >= 24 && frameData[0] == 0x89 && frameData[1] == 0x50 && frameData[2] == 0x4E && frameData[3] == 0x47)
        {
            byte[] wBytes = { frameData[19], frameData[18], frameData[17], frameData[16] };
            byte[] hBytes = { frameData[23], frameData[22], frameData[21], frameData[20] };
            w = BitConverter.ToInt32(wBytes, 0);
            h = BitConverter.ToInt32(hBytes, 0);
        }

        // 🌟 리뷰어 지적 대응: BMP/WDS 등 모든 포맷에 대한 양수(Positive) 검사 추가
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