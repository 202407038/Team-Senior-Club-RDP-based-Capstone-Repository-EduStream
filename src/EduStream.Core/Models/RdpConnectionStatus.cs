namespace EduStream.Core.Models;

/// <summary>
/// RDP 공급자에 종속되지 않는 연결 상태 계약입니다.
/// ConnectionId는 재접속 시 새로 생성하여 이전 연결 콜백을 배제합니다.
/// 자격 증명/초대 토큰 및 RDP 화면 데이터는 이 모델에 포함하지 않습니다.
/// </summary>
public sealed record RdpConnectionStatus
{
    public Guid SessionId { get; }
    public string ParticipantId { get; }
    public Guid ConnectionId { get; }
    public RdpConnectionState State { get; }
    public RdpFailureReason Failure { get; }
    public bool CanRetry => State == RdpConnectionState.Failed &&
        Failure is RdpFailureReason.NetworkInterrupted or RdpFailureReason.HostUnavailable;

    private RdpConnectionStatus(Guid sessionId, string participantId, Guid connectionId,
        RdpConnectionState state, RdpFailureReason failure)
    {
        SessionId = sessionId;
        ParticipantId = participantId;
        ConnectionId = connectionId;
        State = state;
        Failure = failure;
    }

    public static RdpConnectionStatus Create(Guid sessionId, string participantId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("강의 세션 ID가 필요합니다.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(participantId)) throw new ArgumentException("참가자 ID가 필요합니다.", nameof(participantId));
        return new(sessionId, participantId, Guid.Empty, RdpConnectionState.Idle, RdpFailureReason.None);
    }

    public RdpConnectionStatus BeginConnect(Guid connectionId)
    {
        if (State != RdpConnectionState.Idle && !CanRetry)
            throw new InvalidOperationException("현재 상태에서는 연결을 시작할 수 없습니다.");
        if (connectionId == Guid.Empty || connectionId == ConnectionId)
            throw new ArgumentException("연결 시도마다 새 ID가 필요합니다.", nameof(connectionId));
        return new(SessionId, ParticipantId, connectionId,
            State == RdpConnectionState.Idle ? RdpConnectionState.Connecting : RdpConnectionState.Reconnecting,
            RdpFailureReason.None);
    }

    public RdpConnectionStatus Connected(Guid connectionId)
    {
        RequireCurrent(connectionId);
        if (State is not (RdpConnectionState.Connecting or RdpConnectionState.Reconnecting))
            throw new InvalidOperationException("연결 중인 상태에서만 연결 완료를 적용합니다.");
        return new(SessionId, ParticipantId, ConnectionId, RdpConnectionState.Connected, RdpFailureReason.None);
    }

    public RdpConnectionStatus Fail(Guid connectionId, RdpFailureReason reason)
    {
        RequireCurrent(connectionId);
        if (State is not (RdpConnectionState.Connecting or RdpConnectionState.Reconnecting or RdpConnectionState.Connected))
            throw new InvalidOperationException("활성 연결에만 실패를 적용합니다.");
        if (!Enum.IsDefined(reason) || reason == RdpFailureReason.None)
            throw new ArgumentOutOfRangeException(nameof(reason));
        return new(SessionId, ParticipantId, ConnectionId, RdpConnectionState.Failed, reason);
    }

    public RdpConnectionStatus Close() =>
        new(SessionId, ParticipantId, ConnectionId, RdpConnectionState.Closed, RdpFailureReason.None);

    private void RequireCurrent(Guid connectionId)
    {
        if (connectionId == Guid.Empty || connectionId != ConnectionId)
            throw new InvalidOperationException("이전 연결의 이벤트는 현재 연결에 적용할 수 없습니다.");
    }
}
