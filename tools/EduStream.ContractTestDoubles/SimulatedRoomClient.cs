using EduStream.Core.Collaboration;

namespace EduStream.ContractTestDoubles;

/// <summary>UI 개발/테스트 전용 대역. 실제 인증/암호화/네트워크 동작이 아닙니다. 단일 스레드용.</summary>
public sealed class SimulatedRoomClient : IRoomSessionClient
{
    public bool IsSimulation => true;
    public CollaborationError? JoinFailure { get; set; }
    public RoomJoined? Current { get; private set; }
    public event Action<RoomJoined>? ParticipantsChanged;

    public Task<RoomJoined> JoinAsync(RoomJoinRequest request, ReadOnlyMemory<char> password,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request.Validate();
        if (JoinFailure is { } error) throw new CollaborationException(error);
        if (Current is not null) throw new CollaborationException(CollaborationError.InvalidRequest);
        // 비밀번호는 대역에서도 저장/비교/출력하지 않습니다.
        var connection = new ParticipantConnection(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
        Current = new(connection, 1, new[]
        {
            new ParticipantSnapshot(connection, request.DisplayName, true, true, true, 1)
        });
        ParticipantsChanged?.Invoke(Current);
        return Task.FromResult(Current);
    }

    public Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Current is not null)
        {
            var previous = Current;
            Current = null;
            ParticipantsChanged?.Invoke(previous with { Revision = previous.Revision + 1,
                Participants = Array.Empty<ParticipantSnapshot>() });
        }
        return Task.CompletedTask;
    }

    public Task SetPermissionsAsync(bool allowViewing, bool allowControl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Current ?? throw new CollaborationException(CollaborationError.SessionClosed);
        Current = current with
        {
            Revision = current.Revision + 1,
            Participants = current.Participants.Select(p => p.Connection == current.Connection
                ? p with { AllowViewing = allowViewing, AllowControl = allowViewing && allowControl,
                    PermissionRevision = p.PermissionRevision + 1 } : p).ToArray()
        };
        ParticipantsChanged?.Invoke(Current);
        return Task.CompletedTask;
    }

    public void SetParticipantsForTest(IReadOnlyList<ParticipantSnapshot> participants)
    {
        var current = Current ?? throw new CollaborationException(CollaborationError.SessionClosed);
        var next = current with { Revision = current.Revision + 1, Participants = participants.ToArray() };
        ParticipantSnapshotRules.Validate(next);
        Current = next;
        ParticipantsChanged?.Invoke(next);
    }
}
