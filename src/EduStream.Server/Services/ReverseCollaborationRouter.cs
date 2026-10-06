using System.Text.Json;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;

namespace EduStream.Server.Services;

/// <summary>
/// 학생이 보낸 역방향 초대와 비밀번호가 현재 연결·학생 공유 세대와 모두 일치한 상태입니다.
/// 교수자 viewer(3번)는 이 값으로만 접속하며, 받았다는 사실이 접속 성공을 뜻하지는 않습니다.
/// </summary>
public sealed record ReverseInvitationDelivery(ParticipantConnection Student, string DisplayName,
    ReverseRdpInvitationNotice Invitation, ReverseRdpInvitationSecretNotice Secret)
{
    public override string ToString() =>
        $"ReverseInvitationDelivery {{ Student = {DisplayName}, InvitationId = {Invitation.InvitationId}, SharingId = {Invitation.SharingId}, Secret = *** }}";
}

/// <summary>
/// 2번 구현: Kind 15·16(학생→교수자 역방향 초대/비밀번호) 수신 대조와 Kind 17(교수자→학생 판서) 송신·재접속 복원을 담당합니다.
/// 기대 신원은 수신 payload가 아니라 레지스트리의 인증된 연결과 <see cref="ReverseRdpIdentity"/> 규칙에서 얻습니다.
/// </summary>
/// <remarks>
/// 현재 단계에서는 capability 협상이 없어 상대 앱 버전을 구분하지 않습니다. 같은 배포본끼리만 연결한다는 전제이며,
/// 구버전 학생 앱은 Kind 17을 처리하지 않고 무시합니다(판서가 보이지 않을 뿐 연결은 유지).
/// </remarks>
public sealed class ReverseCollaborationRouter : IDisposable
{
    /// <summary>재접속 복원용으로 보관하는 판서 프레임 상한. 마지막 레이어 스냅샷 이후 스트로크만 쌓입니다.</summary>
    public const int MaxAnnotationReplayFrames = AnnotationTransportNotice.MaxStrokes;
    public const long MaxAnnotationReplayBytes = 32L * 1024 * 1024;
    // 학생 한 명이 바꾼 공유 세대·초대 ID 기억 상한. 잘못된 학생 앱이 무한히 늘리지 못하게 한다.
    private const int MaxRememberedIds = 128;

    private sealed class StudentState
    {
        public Guid SharingId;
        public readonly BoundedIdSet RetiredSharings = new();
        public readonly BoundedIdSet SeenInvitations = new();
        public ReverseRdpInvitationNotice? Pending;
        public ReverseInvitationDelivery? Ready;
    }

    private sealed class BoundedIdSet
    {
        private readonly HashSet<Guid> _ids = new();
        private readonly Queue<Guid> _order = new();

        public bool Contains(Guid id) => _ids.Contains(id);

        public void Add(Guid id)
        {
            if (!_ids.Add(id)) return;
            _order.Enqueue(id);
            if (_order.Count > MaxRememberedIds) _ids.Remove(_order.Dequeue());
        }
    }

    private sealed record AnnotationPeer(ParticipantConnection Connection, ICollaborationChannel Channel);

    private readonly object _gate = new();
    private readonly ParticipantConnection _professor;
    private readonly string _professorId;
    private readonly ParticipantRegistry _registry;
    private readonly ILogSink _logSink;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<Guid, StudentState> _students = new(); // 학생 connectionId → 역방향 상태
    private readonly Dictionary<Guid, AnnotationPeer> _annotationPeers = new(); // 학생 connectionId → 판서 수신 채널
    // 상태 변경과 같은 순서로 알림을 꺼내되 외부 콜백은 상태 잠금 밖에서 실행한다.
    private readonly Queue<Action> _invitationNotifications = new();
    private bool _dispatchingInvitations;
    // 공유 전환·복원·실시간 송신을 같은 큐에 넣어 중지 알림을 이전 송신이 추월하지 못하게 한다.
    private Task _annotationTail = Task.CompletedTask;
    private readonly LinkedList<byte[]> _annotationReplay = new();
    private long _annotationReplayBytes;
    private bool _annotationReplayTruncated;
    private Guid _annotationSharingId;
    private long _annotationSequence;
    private bool _disposed;

    /// <summary>초대와 비밀번호 대조가 모두 끝났을 때 발생합니다. 비밀번호는 이 값으로만 교수자 viewer에 전달합니다.</summary>
    public event Action<ReverseInvitationDelivery>? InvitationReady;

    /// <summary>
    /// 전달했던 초대가 더 이상 유효하지 않을 때 발생합니다(학생 공유 재시작·보기 허용 철회·이탈·끊김·세션 종료).
    /// 교수자 viewer는 이 알림을 받으면 해당 학생 연결을 닫아야 합니다.
    /// </summary>
    public event Action<ParticipantConnection, Guid>? InvitationWithdrawn;

    public ReverseCollaborationRouter(ParticipantConnection professor, ParticipantRegistry registry, ILogSink logSink,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(professor);
        professor.Validate();
        if (professor.Role != ParticipantRole.Professor) throw new ArgumentException("교수자 연결이 필요합니다.", nameof(professor));
        _professor = professor;
        _professorId = ReverseRdpIdentity.For(professor);
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _registry.ConnectionRemoved += OnConnectionRemoved;
        _registry.PermissionsChanged += OnPermissionsChanged;
    }

    /// <summary>역방향 초대의 ProfessorId로 써야 하는 값입니다. 3번 제어 게이트의 교수자 ID 해석에도 같은 값을 씁니다.</summary>
    public string ProfessorId => _professorId;

    /// <summary>현재 판서 공유 ID. 교수자 화면 공유가 멈춰 있으면 Guid.Empty입니다.</summary>
    public Guid AnnotationSharingId
    {
        get { lock (_gate) return _annotationSharingId; }
    }

    /// <summary>보관 상한을 넘어 오래된 스트로크를 버렸으면 false입니다. 이때 재접속 학생은 일부 판서를 받지 못합니다.</summary>
    public bool IsAnnotationReplayComplete
    {
        get { lock (_gate) return !_annotationReplayTruncated; }
    }

    public int AnnotationReplayCount
    {
        get { lock (_gate) return _annotationReplay.Count; }
    }

    /// <summary>
    /// 해당 학생 연결의 접속 가능한 초대를 반환합니다. 만료됐거나 연결·보기 허용이 바뀌었으면 null입니다.
    /// </summary>
    public ReverseInvitationDelivery? TryGetInvitation(Guid studentConnectionId)
    {
        ReverseInvitationDelivery? ready;
        lock (_gate) ready = _students.TryGetValue(studentConnectionId, out var state) ? state.Ready : null;
        if (ready is null || ready.Invitation.ExpiresAt <= _clock() || !CanViewStudent(ready.Student)) return null;
        return ready;
    }

    /// <summary>초대의 신규 접속 유효기간과 이미 연결된 viewer 수명을 구분한다. 실제 접속 확인된 현재 세대만 허용한다.</summary>
    public Guid? TryGetConnectedSharing(Guid studentConnectionId, Guid confirmedSharingId)
    {
        ReverseInvitationDelivery? ready;
        lock (_gate) ready = _students.TryGetValue(studentConnectionId, out var state) ? state.Ready : null;
        return ready is not null && ready.Invitation.SharingId == confirmedSharingId && CanViewStudent(ready.Student)
            ? confirmedSharingId : null;
    }

    /// <summary>
    /// 학생 연결에서 받은 Kind 15·16을 처리합니다. 거부하면 해당 학생에게만 실패 알림(RequestId=InvitationId)을 보냅니다.
    /// </summary>
    public async Task HandleFrameAsync(ParticipantConnection student, ICollaborationChannel channel, byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(student);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(frame);

        var invitationId = Guid.Empty;
        try
        {
            switch (CollaborationFrameInspector.PeekKind(frame))
            {
                case CollaborationMessageKind.ReverseRdpInvitation:
                    var invitation = CollaborationMessageCodec.Decode<ReverseRdpInvitationNotice>(frame, out _);
                    invitationId = invitation.InvitationId;
                    AcceptInvitation(student, invitation);
                    break;
                case CollaborationMessageKind.ReverseRdpInvitationSecret:
                    var secret = CollaborationMessageCodec.Decode<ReverseRdpInvitationSecretNotice>(frame, out _);
                    invitationId = secret.InvitationId;
                    AcceptSecret(student, secret);
                    break;
                default:
                    throw new CollaborationException(CollaborationError.InvalidRequest);
            }
        }
        catch (CollaborationException ex)
        {
            _logSink.Write($"[Reverse] 역방향 초대 거부: connection={student.ConnectionId}, invitation={invitationId}, 사유={ex.Code}");
            if (invitationId != Guid.Empty)
                await SendFailureAsync(student, channel, invitationId, ex.Code);
        }
    }

    private void AcceptInvitation(ParticipantConnection student, ReverseRdpInvitationNotice invitation)
    {
        RequireViewableStudent(student);
        var now = _clock();
        ReverseInvitationDelivery? withdrawn = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireViewableStudent(student);
            var state = GetStateLocked(student.ConnectionId);
            if (state.RetiredSharings.Contains(invitation.SharingId))
                throw new CollaborationException(CollaborationError.StaleConnection);
            if (state.SeenInvitations.Contains(invitation.InvitationId))
                throw new CollaborationException(CollaborationError.InvalidRequest);
            try
            {
                // 학생 공유 세대는 서버가 미리 알 수 없어 수신 값을 쓰되, 위에서 폐기된 세대는 이미 걸렀다.
                invitation.ValidateForSharing(student.SessionId, invitation.SharingId, _professorId,
                    student.ConnectionId, ReverseRdpIdentity.For(student), now);
            }
            catch (ArgumentException)
            {
                throw new CollaborationException(CollaborationError.StaleConnection);
            }

            // 학생이 역방향 공유를 다시 시작하면 이전 세대의 초대·비밀번호는 모두 폐기한다.
            if (state.SharingId != Guid.Empty && state.SharingId != invitation.SharingId)
                state.RetiredSharings.Add(state.SharingId);
            state.SharingId = invitation.SharingId;
            state.SeenInvitations.Add(invitation.InvitationId);
            withdrawn = state.Ready;
            state.Ready = null;
            state.Pending = invitation;
            if (withdrawn is not null) QueueWithdrawnLocked(withdrawn);
        }
        DrainInvitationNotifications();
        _logSink.Write($"[Reverse] 역방향 초대 수신(비밀번호 대기): connection={student.ConnectionId}, invitation={invitation.InvitationId}");
    }

    private void AcceptSecret(ParticipantConnection student, ReverseRdpInvitationSecretNotice secret)
    {
        var displayName = RequireViewableStudent(student);
        var now = _clock();
        ReverseInvitationDelivery ready;
        lock (_gate)
        {
            ThrowIfDisposed();
            RequireViewableStudent(student);
            if (!_students.TryGetValue(student.ConnectionId, out var state) || state.Pending is null ||
                state.Pending.InvitationId != secret.InvitationId)
                throw new CollaborationException(CollaborationError.StaleConnection);
            try
            {
                secret.ValidateForInvitation(state.Pending, now);
            }
            catch (ArgumentException)
            {
                // 비밀번호가 맞지 않는 초대는 다시 쓰지 않는다. 학생은 새 초대를 만들어야 한다.
                state.Pending = null;
                throw new CollaborationException(CollaborationError.StaleConnection);
            }
            ready = new ReverseInvitationDelivery(student, displayName, state.Pending, secret);
            state.Pending = null;
            state.Ready = ready;
            _invitationNotifications.Enqueue(() =>
            {
                // 회수/종료가 알림 실행보다 빨랐으면 취소된 초대는 전달하지 않는다.
                if (TryGetInvitation(student.ConnectionId) == ready)
                    NotifyEach(InvitationReady, handler => handler(ready));
            });
        }
        _logSink.Write($"[Reverse] 역방향 초대 준비: 학생={displayName}, invitation={secret.InvitationId}");
        DrainInvitationNotifications();
    }

    /// <summary>
    /// 교수자 화면 공유가 (재)시작될 때 호출합니다. 이전 공유의 판서 복원 기록은 버립니다.
    /// </summary>
    public void BeginAnnotationSharing(Guid sharingId)
    {
        CollaborationContract.RequireId(sharingId, nameof(sharingId));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_annotationSharingId == sharingId) return;
            _annotationSharingId = sharingId;
            ClearReplayLocked();
            QueueAnnotationResetLocked(sharingId, remember: true);
        }
        _logSink.Write($"[Annotation] 판서 공유 시작: sharingId={sharingId}");
    }

    /// <summary>교수자 화면 공유가 멈추면 호출합니다. 이후 판서 송신은 공유가 다시 붙을 때까지 거부됩니다.</summary>
    public void EndAnnotationSharing()
    {
        lock (_gate)
        {
            if (_annotationSharingId == Guid.Empty) return;
            var ended = _annotationSharingId;
            _annotationSharingId = Guid.Empty;
            ClearReplayLocked();
            QueueAnnotationResetLocked(ended, remember: false);
        }
        _logSink.Write("[Annotation] 판서 공유 종료");
    }

    /// <summary>
    /// 학생 연결을 판서 수신자로 붙이고, 현재 공유의 판서 기록을 원래 순서대로 다시 보냅니다(재접속 복원).
    /// </summary>
    public Task AttachAnnotationPeerAsync(ParticipantConnection student, ICollaborationChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(student);
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsCurrentStudent(student)) throw new CollaborationException(CollaborationError.NotAuthorized);

        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentStudent(student)) throw new CollaborationException(CollaborationError.StaleConnection);
            var replay = _annotationReplay.ToArray();
            var peer = new AnnotationPeer(student, channel);
            _annotationPeers[student.ConnectionId] = peer;
            return QueueAnnotationsLocked(async () =>
            {
                try
                {
                    if (!IsCurrentStudent(student))
                        throw new CollaborationException(CollaborationError.StaleConnection);
                    foreach (var frame in replay)
                        if (!await TrySendAsync(peer, frame, cancellationToken).ConfigureAwait(false))
                            throw new CollaborationException(CollaborationError.StaleConnection);
                    return replay.Length;
                }
                catch
                {
                    lock (_gate)
                        if (_annotationPeers.TryGetValue(student.ConnectionId, out var current) && ReferenceEquals(current, peer))
                            _annotationPeers.Remove(student.ConnectionId);
                    throw;
                }
            });
        }
    }

    /// <summary>
    /// 교수자 판서 엔진이 낸 스트로크/레이어 JSON을 현재 공유의 판서로 모든 학생에게 보냅니다.
    /// 공유가 없거나 형식이 잘못됐으면 CollaborationException을 던지며 조용히 버리지 않습니다.
    /// </summary>
    /// <returns>전달에 성공한 학생 수. 송신 성공은 학생 화면 적용 성공을 뜻하지 않습니다.</returns>
    public Task<int> PublishAnnotationAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        lock (_gate)
        {
            if (_disposed || _annotationSharingId == Guid.Empty)
                return Task.FromException<int>(new CollaborationException(CollaborationError.SessionClosed));
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<int>(cancellationToken);
            byte[] frame;
            var notice = new AnnotationTransportNotice(_professor.SessionId, _annotationSharingId,
                _professor.ConnectionId, _annotationSequence + 1, payloadJson);
            try
            {
                frame = CollaborationMessageCodec.Encode(Guid.NewGuid(), notice);
            }
            catch (ArgumentException)
            {
                return Task.FromException<int>(new CollaborationException(CollaborationError.InvalidRequest));
            }
            _annotationSequence = notice.Sequence;
            Remember(frame, IsLayerSnapshot(payloadJson));
            var peers = _annotationPeers.Values.ToArray();
            return QueueAnnotationsLocked(() => BroadcastAnnotationAsync(peers, frame, cancellationToken));
        }
    }

    /// <summary>호출 시점까지 예약된 공유 초기화·복원·판서 송신을 기다립니다. UI 스레드를 동기 대기하지 않습니다.</summary>
    public Task WaitForAnnotationDeliveryAsync()
    {
        lock (_gate) return _annotationTail;
    }

    private Task<T> QueueAnnotationsLocked<T>(Func<Task<T>> operation)
    {
        var previous = _annotationTail;
        var next = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); }
            catch { /* 이전 요청의 실패가 이후 공유 중지/초기화를 막아서는 안 된다. */ }
            return await operation().ConfigureAwait(false);
        });
        _annotationTail = next;
        // 동기 수명 API에서 예약한 작업도 예외를 관찰한다. 각 요청 Task의 실패 상태는 유지한다.
        _ = next.ContinueWith(task => _logSink.Write(
            $"[Annotation] 예약 전달 실패: {task.Exception!.GetBaseException().GetType().Name}"),
            TaskContinuationOptions.OnlyOnFaulted);
        return next;
    }

    private void QueueAnnotationResetLocked(Guid sharingId, bool remember)
    {
        // 기존 Kind 17 레이어 규격의 빈 스냅샷을 사용한다. 새 스트로크가 없어도 학생 화면을 지운다.
        const string emptyLayer = """{"Kind":"annotation-layer","Version":1,"Change":0,"IsVisible":true,"ContentRevision":0,"Strokes":[]}""";
        var notice = new AnnotationTransportNotice(_professor.SessionId, sharingId,
            _professor.ConnectionId, ++_annotationSequence, emptyLayer);
        var frame = CollaborationMessageCodec.Encode(Guid.NewGuid(), notice);
        if (remember) Remember(frame, layerSnapshot: true);
        var peers = _annotationPeers.Values.ToArray();
        QueueAnnotationsLocked(() => BroadcastAnnotationAsync(peers, frame, CancellationToken.None));
    }

    private async Task<int> BroadcastAnnotationAsync(AnnotationPeer[] peers, byte[] frame, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(peers.Select(peer => TrySendAsync(peer, frame, cancellationToken))).ConfigureAwait(false);
        return results.Count(sent => sent);
    }

    private async Task<bool> TrySendAsync(AnnotationPeer peer, byte[] frame, CancellationToken cancellationToken)
    {
        try
        {
            lock (_gate)
                if (_disposed || !_annotationPeers.TryGetValue(peer.Connection.ConnectionId, out var current) ||
                    !ReferenceEquals(current, peer)) return false;
            if (!IsCurrentStudent(peer.Connection)) return false;
            await peer.Channel.SendAsync(frame, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 끊긴 학생은 레지스트리 정리로 떨어지고 재접속 시 복원 기록으로 다시 받는다.
            _logSink.Write($"[Annotation] 판서 전달 실패: connection={peer.Connection.ConnectionId}, {ex.GetType().Name}");
            return false;
        }
    }

    private void Remember(byte[] frame, bool layerSnapshot)
    {
        // 레이어 스냅샷(전체 삭제·Undo·숨김·재표시)은 그 시점의 전체 상태이므로 이전 스트로크 기록을 대체한다.
        if (layerSnapshot) ClearReplayLocked();
        _annotationReplay.AddLast(frame);
        _annotationReplayBytes += frame.Length;
        while (_annotationReplay.Count > MaxAnnotationReplayFrames || _annotationReplayBytes > MaxAnnotationReplayBytes)
        {
            _annotationReplayBytes -= _annotationReplay.First!.Value.Length;
            _annotationReplay.RemoveFirst();
            if (!_annotationReplayTruncated)
                _logSink.Write("[Annotation] 복원 기록 상한 초과: 재접속 학생은 오래된 판서 일부를 받지 못합니다.");
            _annotationReplayTruncated = true;
        }
    }

    private void ClearReplayLocked()
    {
        _annotationReplay.Clear();
        _annotationReplayBytes = 0;
        _annotationReplayTruncated = false;
    }

    private static bool IsLayerSnapshot(string payloadJson)
    {
        // 구조 검사는 Encode에서 끝났다. 여기서는 레이어/스트로크 구분만 한다.
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty("Kind", out _);
    }

    private string RequireViewableStudent(ParticipantConnection student)
    {
        student.Validate();
        if (student.Role != ParticipantRole.Student || student.SessionId != _professor.SessionId)
            throw new CollaborationException(CollaborationError.NotAuthorized);
        var snapshot = _registry.TryResolve(student.ConnectionId);
        if (snapshot is null || snapshot.Connection != student || !snapshot.Connected)
            throw new CollaborationException(CollaborationError.StaleConnection);
        // 역방향 화면은 학생이 '내 화면 보기 허용'을 켠 동안만 교수자에게 전달한다(U06/U07).
        if (!snapshot.AllowViewing)
            throw new CollaborationException(CollaborationError.PermissionDenied);
        return snapshot.DisplayName;
    }

    private bool CanViewStudent(ParticipantConnection student) =>
        _registry.TryResolve(student.ConnectionId) is { Connected: true, AllowViewing: true } snapshot &&
        snapshot.Connection == student;

    private bool IsCurrentStudent(ParticipantConnection student) =>
        student.Role == ParticipantRole.Student && student.SessionId == _professor.SessionId &&
        _registry.TryResolve(student.ConnectionId)?.Connection == student;

    private StudentState GetStateLocked(Guid connectionId)
    {
        if (!_students.TryGetValue(connectionId, out var state))
            _students[connectionId] = state = new StudentState();
        return state;
    }

    private void OnConnectionRemoved(ParticipantConnection connection)
    {
        StudentState? state;
        lock (_gate)
        {
            _students.Remove(connection.ConnectionId, out state);
            _annotationPeers.Remove(connection.ConnectionId);
            if (state?.Ready is { } ready) QueueWithdrawnLocked(ready);
        }
        DrainInvitationNotifications();
    }

    private void OnPermissionsChanged(ParticipantSnapshot snapshot)
    {
        if (snapshot.AllowViewing) return;
        ReverseInvitationDelivery? withdrawn = null;
        lock (_gate)
        {
            if (!_students.TryGetValue(snapshot.Connection.ConnectionId, out var state)) return;
            withdrawn = state.Ready;
            state.Ready = null;
            state.Pending = null;
            if (withdrawn is not null) QueueWithdrawnLocked(withdrawn);
        }
        DrainInvitationNotifications();
    }

    private void QueueWithdrawnLocked(ReverseInvitationDelivery delivery) =>
        _invitationNotifications.Enqueue(() =>
        {
            _logSink.Write($"[Reverse] 역방향 초대 회수: 학생={delivery.DisplayName}, invitation={delivery.Invitation.InvitationId}");
            NotifyEach(InvitationWithdrawn, handler => handler(delivery.Student, delivery.Invitation.InvitationId));
        });

    private void NotifyEach<T>(T? handlers, Action<T> invoke) where T : Delegate
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<T>())
            try { invoke(handler); }
            catch (Exception ex) { _logSink.Write($"[Reverse] 초대 알림 소비 실패: {ex.GetType().Name}"); }
    }

    private void DrainInvitationNotifications()
    {
        lock (_gate)
        {
            if (_dispatchingInvitations) return;
            _dispatchingInvitations = true;
        }
        while (true)
        {
            Action next;
            lock (_gate)
            {
                if (_invitationNotifications.Count == 0)
                {
                    _dispatchingInvitations = false;
                    return;
                }
                next = _invitationNotifications.Dequeue();
            }
            try { next(); }
            catch (Exception ex) { _logSink.Write($"[Reverse] 초대 알림 실패: {ex.GetType().Name}"); }
        }
    }

    private async Task SendFailureAsync(ParticipantConnection student, ICollaborationChannel channel, Guid invitationId,
        CollaborationError error)
    {
        try
        {
            await channel.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(),
                new CollaborationFailureNotice(invitationId, error)));
        }
        catch (Exception ex)
        {
            _logSink.Write($"[Reverse] 거부 알림 전달 실패: connection={student.ConnectionId}, {ex.GetType().Name}");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new CollaborationException(CollaborationError.SessionClosed);
    }

    public void Dispose()
    {
        ReverseInvitationDelivery[] withdrawn;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            withdrawn = _students.Values.Select(state => state.Ready).OfType<ReverseInvitationDelivery>().ToArray();
            foreach (var ready in withdrawn) QueueWithdrawnLocked(ready);
            _students.Clear();
            _annotationPeers.Clear();
            _annotationSharingId = Guid.Empty;
            ClearReplayLocked();
        }
        _registry.ConnectionRemoved -= OnConnectionRemoved;
        _registry.PermissionsChanged -= OnPermissionsChanged;
        DrainInvitationNotifications();
    }
}
