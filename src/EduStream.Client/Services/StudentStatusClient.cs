using EduStream.Core.Collaboration;
using EduStream.Core.Logging;

namespace EduStream.Client.Services;

/// <summary>학생 화면에 표시할 본인 상태입니다.</summary>
public sealed record StudentStatus(bool AllowViewing, bool AllowControl, ControlPhase ControlPhase)
{
    // 교수자 제어 허용은 기본 ON이다(U07). 서버의 첫 상태가 오기 전 표시값이다.
    public static StudentStatus Initial { get; } = new(true, true, ControlPhase.Idle);

    public bool UnderControl => ControlPhase == ControlPhase.Active;
}

/// <summary>
/// 2번 구현: 보호 채널로 받은 학생 본인의 보기/제어 허용 상태와 원격 제어 진행 상태를 유지하고,
/// 학생의 허용 변경을 교수자에게 보냅니다. 늦게 도착한 옛 상태는 revision/sequence로 버립니다.
/// </summary>
public sealed class StudentStatusClient
{
    private readonly object _gate = new();
    private readonly Core.Collaboration.ICollaborationChannel _server;
    private readonly ILogSink _logSink;
    private readonly Guid _sessionId;
    private RoomJoined? _room;
    private long _controlSequence;
    private StudentStatus _status = StudentStatus.Initial;

    public event Action<StudentStatus>? StatusChanged;

    /// <summary>같은 연결의 더 새로운 상태 스냅샷을 받아들인 뒤 발생합니다. 역방향 신원(교수자 연결 포함) 갱신에 씁니다.</summary>
    public event Action<RoomJoined>? RoomChanged;

    public StudentStatusClient(Guid sessionId, Core.Collaboration.ICollaborationChannel server, ILogSink logSink)
    {
        CollaborationContract.RequireId(sessionId, nameof(sessionId));
        _sessionId = sessionId;
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
    }

    public StudentStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>이 클라이언트가 처리하는 메시지면 true입니다. 나머지는 호출자가 다른 처리기로 넘깁니다.</summary>
    public static bool Handles(CollaborationMessageKind kind) =>
        kind is CollaborationMessageKind.Participants or CollaborationMessageKind.ControlStatus;

    public void HandleFrame(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        try
        {
            switch (CollaborationFrameInspector.PeekKind(frame))
            {
                case CollaborationMessageKind.Participants:
                    Apply(CollaborationMessageCodec.Decode<RoomJoined>(frame, out _));
                    break;
                case CollaborationMessageKind.ControlStatus:
                    Apply(CollaborationMessageCodec.Decode<ControlStatusNotice>(frame, out _));
                    break;
                default:
                    throw new CollaborationException(CollaborationError.InvalidRequest);
            }
        }
        catch (CollaborationException ex)
        {
            _logSink.Write($"[Status] 잘못된 상태 메시지 무시: 사유={ex.Code}");
        }
    }

    /// <summary>
    /// 보기/제어 허용을 바꿉니다. 화면 상태는 교수자가 반영한 뒤 돌려준 상태로만 바뀝니다.
    /// 보기를 끄면 제어도 함께 꺼집니다.
    /// </summary>
    public Task SetPermissionsAsync(bool allowViewing, bool allowControl, CancellationToken cancellationToken = default) =>
        _server.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(),
            new PermissionChangeRequest(Guid.NewGuid(), allowViewing, allowViewing && allowControl)), cancellationToken);

    private void Apply(RoomJoined room)
    {
        StudentStatus changed;
        lock (_gate)
        {
            if (room.Connection.SessionId != _sessionId || room.Connection.Role != ParticipantRole.Student)
                return;
            // 첫 상태는 연결을 정하고, 이후에는 같은 연결의 더 새로운 revision만 받는다.
            if (_room is not null && !ParticipantSnapshotRules.IsNewer(_room, room)) return;
            var self = room.Participants.SingleOrDefault(participant => participant.Connection == room.Connection);
            if (self is null) return;
            _room = room;
            changed = _status = _status with { AllowViewing = self.AllowViewing, AllowControl = self.AllowControl };
        }
        RoomChanged?.Invoke(room);
        StatusChanged?.Invoke(changed);
    }

    private void Apply(ControlStatusNotice notice)
    {
        StudentStatus changed;
        lock (_gate)
        {
            if (notice.SessionId != _sessionId || notice.Sequence <= _controlSequence) return;
            _controlSequence = notice.Sequence;
            changed = _status = _status with { ControlPhase = notice.Phase };
        }
        StatusChanged?.Invoke(changed);
    }
}
