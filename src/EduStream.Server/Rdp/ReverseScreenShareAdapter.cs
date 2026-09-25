using System;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 역방향 화면 공유 어댑터
/// 학생 화면 WDS 프레임을 교수자가 수신 처리하는 최소 실행 접점
/// ReverseSessionManager의 FrameReceived 이벤트를 처리하여 교수자에게 전달
/// </summary>
public sealed class ReverseScreenShareAdapter
{
    private readonly IReverseSessionManager _reverseSessionManager;
    private readonly List<Func<byte[], Task>> _frameReceivers = new();
    private bool _isAdapterActive = false;

    public bool IsAdapterActive => _isAdapterActive;
    public ReverseSessionState CurrentState => _reverseSessionManager.CurrentState;

    public event EventHandler<AdapterStateChangedEventArgs>? AdapterStateChanged;
    public event EventHandler<FrameProcessedEventArgs>? FrameProcessed;

    public ReverseScreenShareAdapter(IReverseSessionManager reverseSessionManager)
    {
        _reverseSessionManager = reverseSessionManager ?? throw new ArgumentNullException(nameof(reverseSessionManager));

        // ReverseSessionManager의 FrameReceived 이벤트 구독
        _reverseSessionManager.FrameReceived += OnFrameReceived;
    }

    /// <summary>
    /// 어댑터 활성화
    /// </summary>
    public Task ActivateAdapterAsync(CancellationToken cancellationToken = default)
    {
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
    /// 교수자 초대 생성
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
        _frameReceivers.Add(receiver ?? throw new ArgumentNullException(nameof(receiver)));
    }

    /// <summary>
    /// 프레임 수신 핸들러 초기화
    /// </summary>
    public void ClearFrameReceivers()
    {
        _frameReceivers.Clear();
    }

    /// <summary>
    /// 프레임 수신 이벤트 핸들러
    /// </summary>
    private async void OnFrameReceived(object? sender, FrameReceivedEventArgs e)
    {
        if (!_isAdapterActive) return;

        // 프레임 처리 이벤트 발생
        FrameProcessed?.Invoke(this, new FrameProcessedEventArgs
        {
            FrameData = e.FrameData,
            Timestamp = e.Timestamp,
            ProcessedAt = DateTimeOffset.UtcNow
        });

        // 등록된 수신자들에게 프레임 전달
        foreach (var receiver in _frameReceivers)
        {
            try
            {
                await receiver(e.FrameData);
            }
            catch (Exception ex)
            {
                // 개별 수신자의 실패는 다른 수신자에 영향을 주지 않음
                Console.WriteLine($"[ReverseScreenShare] 프레임 수신자 실패: {ex.GetType().Name}");
            }
        }
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
/// 프레임 처리 이벤트 인자
/// </summary>
public sealed class FrameProcessedEventArgs : EventArgs
{
    public byte[] FrameData { get; init; } = Array.Empty<byte>();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ProcessedAt { get; init; } = DateTimeOffset.UtcNow;
}
