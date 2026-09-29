using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 역방향 화면 공유 실제 어댑터
/// 학생 화면 WDS 프레임 바이너리를 파싱하여 해상도 변경을 추적하고,
/// 교수자 측 디스플레이 렌더러 및 프레임 수신 파이프라인에 실시간 전달
/// </summary>
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

    /// <summary>
    /// 어댑터 활성화
    /// </summary>
    public Task ActivateAdapterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _isAdapterActive = true;
        AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs
        {
            IsActive = true,
            SessionState = _reverseSessionManager.CurrentState,
            Timestamp = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 어댑터 비활성화
    /// </summary>
    public Task DeactivateAdapterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _isAdapterActive = false;
        AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs
        {
            IsActive = false,
            SessionState = _reverseSessionManager.CurrentState,
            Timestamp = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 역방향 공유 세션 시작
    /// </summary>
    public async Task<Guid> StartReverseSharingAsync(Guid sessionId, string studentId, CancellationToken cancellationToken = default)
    {
        var sharingId = await _reverseSessionManager.StartReverseSharingAsync(sessionId, studentId, cancellationToken);

        AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs
        {
            IsActive = _isAdapterActive,
            SessionState = _reverseSessionManager.CurrentState,
            Timestamp = DateTimeOffset.UtcNow
        });

        return sharingId;
    }

    /// <summary>
    /// 교수자 초대 패킷 생성
    /// </summary>
    public async Task<ReverseInvitationPacket> CreateProfessorInvitationAsync(
        Guid sessionId,
        Guid sharingId,
        string professorId,
        Guid connectionId,
        string invitationPassword,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        return await _reverseSessionManager.CreateProfessorInvitationAsync(
            sessionId, sharingId, professorId, connectionId, invitationPassword, expiresAt, cancellationToken);
    }

    /// <summary>
    /// 교수자 연결 요청
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _reverseSessionManager.ConnectAsync(cancellationToken);

        AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs
        {
            IsActive = _isAdapterActive,
            SessionState = _reverseSessionManager.CurrentState,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// 연결 성공 처리
    /// </summary>
    public async Task OnConnectedAsync(CancellationToken cancellationToken = default)
    {
        await _reverseSessionManager.OnConnectedAsync(cancellationToken);

        AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs
        {
            IsActive = _isAdapterActive,
            SessionState = _reverseSessionManager.CurrentState,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// 연결 실패 처리
    /// </summary>
    public async Task OnConnectionFailedAsync(CancellationToken cancellationToken = default)
    {
        await _reverseSessionManager.OnConnectionFailedAsync(cancellationToken);

        AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs
        {
            IsActive = _isAdapterActive,
            SessionState = _reverseSessionManager.CurrentState,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// 연결 종료 처리
    /// </summary>
    public async Task OnDisconnectedAsync(CancellationToken cancellationToken = default)
    {
        await _reverseSessionManager.OnDisconnectedAsync(cancellationToken);

        AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs
        {
            IsActive = _isAdapterActive,
            SessionState = _reverseSessionManager.CurrentState,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// 역방향 공유 세션 중지
    /// </summary>
    public async Task StopReverseSharingAsync(CancellationToken cancellationToken = default)
    {
        await _reverseSessionManager.StopReverseSharingAsync(cancellationToken);

        AdapterStateChanged?.Invoke(this, new AdapterStateChangedEventArgs
        {
            IsActive = _isAdapterActive,
            SessionState = _reverseSessionManager.CurrentState,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// 프레임 수신 핸들러 추가
    /// </summary>
    public void AddFrameReceiver(Func<byte[], Task> receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        lock (_pipelineLock)
        {
            _frameReceivers.Add(receiver);
        }
    }

    /// <summary>
    /// 실제 뷰어 화면 디스플레이 핸들러 추가 (프레임 데이터, 가로, 세로)
    /// </summary>
    public void AddDisplayHandler(Func<byte[], int, int, Task> displayHandler)
    {
        ArgumentNullException.ThrowIfNull(displayHandler);
        lock (_pipelineLock)
        {
            _displayHandlers.Add(displayHandler);
        }
    }

    public void ClearFrameReceivers()
    {
        lock (_pipelineLock)
        {
            _frameReceivers.Clear();
        }
    }

    public void ClearDisplayHandlers()
    {
        lock (_pipelineLock)
        {
            _displayHandlers.Clear();
        }
    }

    /// <summary>
    /// 프레임 수신 처리 (해상도 파싱, 이벤트 발생 및 렌더러 파이프라인 전달)
    /// </summary>
    private async void OnFrameReceived(object? sender, FrameReceivedEventArgs e)
    {
        if (!_isAdapterActive || e.FrameData == null || e.FrameData.Length == 0)
        {
            return;
        }

        Interlocked.Increment(ref _totalFramesProcessed);

        // 1. 프레임 바이너리 헤더 파싱을 통한 실제 해상도 추출
        var width = EstimateFrameWidth(e.FrameData);
        var height = EstimateFrameHeight(e.FrameData);
        _currentFrameWidth = width;
        _currentFrameHeight = height;

        // 2. 프레임 처리 완료 이벤트 발생
        FrameProcessed?.Invoke(this, new FrameProcessedEventArgs
        {
            FrameData = e.FrameData,
            Timestamp = e.Timestamp,
            ProcessedAt = DateTimeOffset.UtcNow
        });

        // 3. 디스플레이 표시 이벤트 발생
        FrameDisplayed?.Invoke(this, new FrameDisplayedEventArgs
        {
            FrameData = e.FrameData,
            Width = width,
            Height = height,
            Timestamp = e.Timestamp,
            DisplayedAt = DateTimeOffset.UtcNow
        });

        // 4. 스레드 안전 복사본을 통한 수신자 및 디스플레이 파이프라인 전달
        Func<byte[], Task>[] receiversCopy;
        Func<byte[], int, int, Task>[] displayHandlersCopy;

        lock (_pipelineLock)
        {
            receiversCopy = _frameReceivers.ToArray();
            displayHandlersCopy = _displayHandlers.ToArray();
        }

        foreach (var receiver in receiversCopy)
        {
            try
            {
                await receiver(e.FrameData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ReverseScreenShareAdapter] 수신 핸들러 실행 예외: {ex.Message}");
            }
        }

        foreach (var displayHandler in displayHandlersCopy)
        {
            try
            {
                await displayHandler(e.FrameData, width, height);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ReverseScreenShareAdapter] 디스플레이 핸들러 실행 예외: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 프레임 바이너리로부터 가로 너비 추정/파싱
    /// </summary>
    public static int EstimateFrameWidth(byte[] frameData)
    {
        return ParseFrameDimensions(frameData).width;
    }

    /// <summary>
    /// 프레임 바이너리로부터 세로 높이 추정/파싱
    /// </summary>
    public static int EstimateFrameHeight(byte[] frameData)
    {
        return ParseFrameDimensions(frameData).height;
    }

    /// <summary>
    /// 화면 프레임 바이너리로부터 실제 해상도(Width, Height) 파싱 (BMP / PNG / WDS 스트림 헤더)
    /// </summary>
    private static (int width, int height) ParseFrameDimensions(byte[] frameData)
    {
        if (frameData != null && frameData.Length >= 26 && frameData[0] == 0x42 && frameData[1] == 0x4D) // 'BM' Windows Bitmap
        {
            int w = BitConverter.ToInt32(frameData, 18);
            int h = Math.Abs(BitConverter.ToInt32(frameData, 22));
            if (w > 0 && h > 0) return (w, h);
        }

        if (frameData != null && frameData.Length >= 24 && frameData[0] == 0x89 && frameData[1] == 0x50 && frameData[2] == 0x4E && frameData[3] == 0x47) // PNG
        {
            int w = (frameData[16] << 24) | (frameData[17] << 16) | (frameData[18] << 8) | frameData[19];
            int h = (frameData[20] << 24) | (frameData[21] << 16) | (frameData[22] << 8) | frameData[23];
            if (w > 0 && h > 0) return (w, h);
        }

        if (frameData != null && frameData.Length >= 12 && frameData[0] == 0x57 && frameData[1] == 0x44 && frameData[2] == 0x53) // 'WDS' Header
        {
            int w = BitConverter.ToInt32(frameData, 4);
            int h = BitConverter.ToInt32(frameData, 8);
            if (w > 0 && h > 0) return (w, h);
        }

        return (1920, 1080);
    }
}

/// <summary>
/// 어댑터 상태 변경 이벤트 인자
/// </summary>
public sealed class AdapterStateChangedEventArgs : EventArgs
{
    public bool IsActive { get; init; }
    public ReverseSessionState SessionState { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 프레임 처리 완료 이벤트 인자
/// </summary>
public sealed class FrameProcessedEventArgs : EventArgs
{
    public byte[] FrameData { get; init; } = Array.Empty<byte>();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ProcessedAt { get; init; } = DateTimeOffset.UtcNow;
}
