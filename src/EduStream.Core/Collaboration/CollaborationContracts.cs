namespace EduStream.Core.Collaboration;

/// <summary>새 기능의 프로세스 내 계약. 기존 v1 TCP 패킷으로 자동 전송되지 않습니다.</summary>
public static class CollaborationContract
{
    public const int Version = 1;
    public const string Capability = "edustream.collaboration/1";

    public static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty) throw new ArgumentException("빈 식별자는 허용하지 않습니다.", name);
    }
}

public enum CollaborationError
{
    InvalidRequest, NotAuthorized, SessionClosed, StaleConnection, PermissionDenied,
    UnsupportedCapability, FileUnavailable, SourceChanged, IntegrityFailure,
    TransferIncomplete, ResourceLimit
}

/// <summary>내부 경로·비밀번호를 넣지 않는 오류. IO/취소는 원래 예외로 호출자에게 전달합니다.</summary>
public sealed class CollaborationException(CollaborationError code) : Exception(code.ToString())
{
    public CollaborationError Code { get; } = code;
}

public enum ParticipantRole { Professor, Student }

/// <summary>2번이 인증된 연결에서 생성. 수신 JSON의 SenderId/이름으로 생성하면 안 됩니다.</summary>
public sealed record ParticipantConnection(Guid SessionId, Guid ParticipantId, Guid ConnectionId,
    ParticipantRole Role)
{
    public void Validate()
    {
        CollaborationContract.RequireId(SessionId, nameof(SessionId));
        CollaborationContract.RequireId(ParticipantId, nameof(ParticipantId));
        CollaborationContract.RequireId(ConnectionId, nameof(ConnectionId));
        if (!Enum.IsDefined(Role)) throw new ArgumentOutOfRangeException(nameof(Role));
    }
}

public sealed record ParticipantSnapshot(ParticipantConnection Connection, string DisplayName,
    bool Connected, bool AllowViewing, bool AllowControl, long PermissionRevision);

public sealed record RoomJoinRequest(string Host, int Port, string DisplayName, Guid AttemptId)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 ||
            Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 80)
            throw new CollaborationException(CollaborationError.InvalidRequest);
        CollaborationContract.RequireId(AttemptId, nameof(AttemptId));
    }
}

public sealed record RoomJoined(ParticipantConnection Connection, long Revision,
    IReadOnlyList<ParticipantSnapshot> Participants);

/// <summary>
/// 2번 구현. 비밀번호 없는 방도 교수자 신원/채널 검증은 생략하지 않습니다.
/// password는 로깅/ToString/DTO 직렬화 금지, 호출자와 구현자가 사용 후 수명 정리.
/// </summary>
public interface IRoomSessionClient
{
    event Action<RoomJoined>? ParticipantsChanged;
    Task<RoomJoined> JoinAsync(RoomJoinRequest request, ReadOnlyMemory<char> password,
        CancellationToken cancellationToken = default);
    Task LeaveAsync(CancellationToken cancellationToken = default);
    Task SetPermissionsAsync(bool allowViewing, bool allowControl,
        CancellationToken cancellationToken = default);
}
