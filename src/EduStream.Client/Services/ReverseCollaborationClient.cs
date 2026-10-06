using EduStream.Core.Collaboration;
using EduStream.Core.Logging;

namespace EduStream.Client.Services;

/// <summary>
/// 2번 구현: 학생 쪽 Kind 15·16(역방향 초대/비밀번호 송신)과 Kind 17(교수자 판서 수신 대조)을 담당합니다.
/// 교수자 신원과 학생 본인 연결은 보호 채널로 받은 상태 스냅샷에서만 얻고, 수신 판서의 주장값으로 정하지 않습니다.
/// </summary>
/// <remarks>
/// 실제 역방향 WDS 호스트 생성과 판서 렌더러 연결은 3번 소비 작업입니다. 이 클래스는 <see cref="Target"/>으로 초대에 넣을 값을 주고,
/// <see cref="AnnotationRenderer"/>에 연결된 수신 레이어 적용이 끝난 뒤에만 마지막 적용 번호를 올립니다.
/// </remarks>
public sealed class ReverseCollaborationClient
{
    private const int MaxRememberedIds = 128;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _annotationOrder = new(1, 1);
    private readonly Guid _sessionId;
    private readonly ICollaborationChannel _server;
    private readonly ILogSink _logSink;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HashSet<Guid> _retiredSharings = new();
    private readonly Queue<Guid> _retiredOrder = new();
    private readonly HashSet<Guid> _sentInvitations = new();
    private readonly Queue<Guid> _sentOrder = new();
    private ParticipantConnection? _self;
    private ParticipantConnection? _professor;
    private Guid _annotationSharingId;
    private long _lastAppliedSequence;

    public ReverseCollaborationClient(Guid sessionId, ICollaborationChannel server, ILogSink logSink,
        Func<DateTimeOffset>? clock = null)
    {
        CollaborationContract.RequireId(sessionId, nameof(sessionId));
        _sessionId = sessionId;
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 3번 판서 수신 레이어 적용(예: ReceiveRemoteStrokeJsonAsync(notice.PayloadJson)). 연결 전에는 받은 판서를 적용하지 않고 기록만 남깁니다.
    /// </summary>
    public Func<AnnotationTransportNotice, Task>? AnnotationRenderer { get; set; }

    /// <summary>교수자가 화면 공유를 다시 시작해 새 판서 세대로 넘어갔을 때 발생합니다. 렌더러는 이전 판서를 지워야 합니다.</summary>
    public event Action<Guid>? AnnotationSharingChanged;

    /// <summary>교수자가 보낸 역방향 초대를 거부했을 때 발생합니다(초대 ID, 사유).</summary>
    public event Action<Guid, CollaborationError>? InvitationRejected;

    /// <summary>상태 스냅샷을 받기 전이면 null입니다. 역방향 초대는 이 값으로만 만듭니다.</summary>
    public ReverseRdpInvitationTarget? Target
    {
        get
        {
            lock (_gate)
            {
                if (_self is null || _professor is null) return null;
                return new ReverseRdpInvitationTarget(_sessionId, _self.ConnectionId,
                    ReverseRdpIdentity.For(_self), ReverseRdpIdentity.For(_professor));
            }
        }
    }

    public Guid AnnotationSharingId
    {
        get { lock (_gate) return _annotationSharingId; }
    }

    public long LastAppliedAnnotationSequence
    {
        get { lock (_gate) return _lastAppliedSequence; }
    }

    public static bool Handles(CollaborationMessageKind kind) => kind == CollaborationMessageKind.Annotation;

    /// <summary><see cref="StudentStatusClient.RoomChanged"/>로 받은 스냅샷에서 본인·교수자 연결을 갱신합니다.</summary>
    public void ApplyRoom(RoomJoined room)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (room.Connection.SessionId != _sessionId || room.Connection.Role != ParticipantRole.Student) return;
        var professors = room.Participants
            .Where(participant => participant.Connection.Role == ParticipantRole.Professor &&
                                  participant.Connection.SessionId == _sessionId)
            .ToArray();
        lock (_gate)
        {
            _self = room.Connection;
            // 교수자 연결이 둘 이상이면 어느 쪽도 믿지 않는다.
            _professor = professors.Length == 1 ? professors[0].Connection : null;
        }
    }

    /// <summary>
    /// 3번 학생 호스트가 만든 역방향 초대와 비밀번호를 현재 연결 기준으로 대조한 뒤 교수자에게 보냅니다.
    /// 송신 성공은 교수자 viewer 접속 성공을 뜻하지 않습니다. 거부되면 <see cref="InvitationRejected"/>가 발생합니다.
    /// </summary>
    public async Task SendInvitationAsync(ReverseRdpInvitationNotice invitation, ReverseRdpInvitationSecretNotice secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentNullException.ThrowIfNull(secret);
        var target = Target ?? throw new CollaborationException(CollaborationError.StaleConnection);
        var now = _clock();
        try
        {
            invitation.ValidateForConnection(target.SessionId, target.ProfessorId, target.ConnectionId, target.StudentId, now);
            secret.ValidateForInvitation(invitation, now);
        }
        catch (ArgumentException)
        {
            throw new CollaborationException(CollaborationError.InvalidRequest);
        }

        lock (_gate) Remember(_sentInvitations, _sentOrder, invitation.InvitationId);
        // 같은 보호 채널은 프레임 순서를 보장하므로 교수자는 항상 초대를 비밀번호보다 먼저 받는다.
        await _server.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), invitation), cancellationToken);
        await _server.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), secret), cancellationToken);
        _logSink.Write($"[Reverse] 역방향 초대 전송: invitation={invitation.InvitationId}");
    }

    /// <summary>내가 보낸 초대에 대한 거부 알림이면 처리하고 true를 반환합니다. 나머지는 파일 처리기 몫입니다.</summary>
    public bool HandleFailure(CollaborationFailureNotice failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            if (!_sentInvitations.Contains(failure.RequestId)) return false;
        }
        _logSink.Write($"[Reverse] 역방향 초대 거부됨: invitation={failure.RequestId}, 사유={failure.Error}");
        InvitationRejected?.Invoke(failure.RequestId, failure.Error);
        return true;
    }

    /// <summary>
    /// Kind 17 판서를 대조해 렌더러에 넘깁니다. 이전 공유·중복/역순 번호·교수자 아닌 연결의 판서는 적용하지 않습니다.
    /// </summary>
    public async Task HandleFrameAsync(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        await _annotationOrder.WaitAsync();
        try
        {
            AnnotationTransportNotice notice;
            Guid? sharingChanged = null;
            try
            {
                notice = CollaborationMessageCodec.Decode<AnnotationTransportNotice>(frame, out _);
                lock (_gate)
                {
                    var professor = _professor ?? throw new CollaborationException(CollaborationError.StaleConnection);
                    if (notice.SharingId == _annotationSharingId)
                    {
                        notice.ValidateForSender(professor, _annotationSharingId, _lastAppliedSequence);
                    }
                    else
                    {
                        // 서버는 현재 공유의 판서만 보낸다. 이미 지나간 공유로 되돌아가는 프레임은 늦게 도착한 것이다.
                        if (_retiredSharings.Contains(notice.SharingId))
                            throw new CollaborationException(CollaborationError.StaleConnection);
                        notice.ValidateForSender(professor, notice.SharingId, 0);
                        if (_annotationSharingId != Guid.Empty)
                            Remember(_retiredSharings, _retiredOrder, _annotationSharingId);
                        _annotationSharingId = notice.SharingId;
                        _lastAppliedSequence = 0;
                        sharingChanged = notice.SharingId;
                    }
                }
            }
            catch (CollaborationException ex)
            {
                _logSink.Write($"[Annotation] 판서 무시: 사유={ex.Code}");
                return;
            }

            if (sharingChanged is { } changed) AnnotationSharingChanged?.Invoke(changed);

            var renderer = AnnotationRenderer;
            if (renderer is null)
            {
                _logSink.Write($"[Annotation] 판서 렌더러 미연결로 적용하지 않음: sequence={notice.Sequence}");
                return;
            }
            try
            {
                await renderer(notice);
            }
            catch (Exception ex)
            {
                // 적용 실패한 번호는 올리지 않는다. 다음 레이어 스냅샷이 오면 전체 상태로 다시 맞춰진다.
                _logSink.Write($"[Annotation] 판서 적용 실패: sequence={notice.Sequence}, {ex.GetType().Name}");
                return;
            }
            lock (_gate)
            {
                if (notice.SharingId == _annotationSharingId && notice.Sequence > _lastAppliedSequence)
                    _lastAppliedSequence = notice.Sequence;
            }
        }
        finally
        {
            _annotationOrder.Release();
        }
    }

    private static void Remember(HashSet<Guid> set, Queue<Guid> order, Guid id)
    {
        if (!set.Add(id)) return;
        order.Enqueue(id);
        if (order.Count > MaxRememberedIds) set.Remove(order.Dequeue());
    }
}
