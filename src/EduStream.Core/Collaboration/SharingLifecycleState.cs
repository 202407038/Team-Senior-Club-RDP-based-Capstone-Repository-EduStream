namespace EduStream.Core.Collaboration;

public enum SharingPhase { Waiting, Starting, Sharing, Paused, Failed, Closed }

/// <summary>강의 수명과 공유 수명을 분리. 이전 시도의 native 콜백은 새 상태에 적용하지 않습니다.</summary>
public sealed record SharingLifecycleState
{
    public Guid SessionId { get; }
    public Guid AttemptId { get; }
    public Guid SharingId { get; }
    public SharingPhase Phase { get; }
    public long Generation { get; }

    private SharingLifecycleState(Guid sessionId, Guid attemptId, Guid sharingId,
        SharingPhase phase, long generation)
        => (SessionId, AttemptId, SharingId, Phase, Generation) =
            (sessionId, attemptId, sharingId, phase, generation);

    public static SharingLifecycleState Create(Guid sessionId)
    {
        CollaborationContract.RequireId(sessionId, nameof(sessionId));
        return new(sessionId, Guid.Empty, Guid.Empty, SharingPhase.Waiting, 0);
    }

    public SharingLifecycleState Begin(Guid attemptId)
    {
        CollaborationContract.RequireId(attemptId, nameof(attemptId));
        if (Phase is not (SharingPhase.Waiting or SharingPhase.Paused or SharingPhase.Failed) ||
            attemptId == AttemptId)
            throw new CollaborationException(CollaborationError.InvalidRequest);
        return new(SessionId, attemptId, Guid.Empty, SharingPhase.Starting, checked(Generation + 1));
    }

    public SharingLifecycleState Started(Guid attemptId, long generation, Guid sharingId)
    {
        RequireCurrent(attemptId, generation);
        CollaborationContract.RequireId(sharingId, nameof(sharingId));
        if (Phase != SharingPhase.Starting)
            throw new CollaborationException(CollaborationError.InvalidRequest);
        return new(SessionId, attemptId, sharingId, SharingPhase.Sharing, Generation);
    }

    public SharingLifecycleState Failed(Guid attemptId, long generation)
    {
        RequireCurrent(attemptId, generation);
        if (Phase is not (SharingPhase.Starting or SharingPhase.Sharing))
            throw new CollaborationException(CollaborationError.InvalidRequest);
        return new(SessionId, AttemptId, Guid.Empty, SharingPhase.Failed, Generation);
    }

    public SharingLifecycleState Pause()
    {
        if (Phase == SharingPhase.Closed) throw new CollaborationException(CollaborationError.SessionClosed);
        return new(SessionId, AttemptId, Guid.Empty, SharingPhase.Paused, Generation);
    }

    public SharingLifecycleState Close() =>
        new(SessionId, AttemptId, Guid.Empty, SharingPhase.Closed, Generation);

    private void RequireCurrent(Guid attemptId, long generation)
    {
        if (Phase == SharingPhase.Closed)
            throw new CollaborationException(CollaborationError.SessionClosed);
        if (attemptId == Guid.Empty || attemptId != AttemptId || generation != Generation)
            throw new CollaborationException(CollaborationError.StaleConnection);
    }
}

/// <summary>학생 목록 이벤트 순서 검증. 메타데이터를 권한 증명으로 사용하지 않습니다.</summary>
public static class ParticipantSnapshotRules
{
    public static bool IsNewer(RoomJoined current, RoomJoined incoming) =>
        current.Connection == incoming.Connection && incoming.Revision > current.Revision;

    public static void Validate(RoomJoined snapshot)
    {
        if (snapshot is null || snapshot.Connection is null)
            throw new CollaborationException(CollaborationError.InvalidRequest);
        snapshot.Connection.Validate();
        if (snapshot.Revision < 0 || snapshot.Participants is null)
            throw new CollaborationException(CollaborationError.InvalidRequest);
        var ids = new HashSet<Guid>();
        foreach (var participant in snapshot.Participants)
        {
            if (participant is null || participant.Connection is null)
                throw new CollaborationException(CollaborationError.InvalidRequest);
            participant.Connection.Validate();
            if (participant.Connection.SessionId != snapshot.Connection.SessionId ||
                participant.PermissionRevision < 0 || string.IsNullOrWhiteSpace(participant.DisplayName) ||
                participant.DisplayName.Length > 80 || !ids.Add(participant.Connection.ParticipantId))
                throw new CollaborationException(CollaborationError.InvalidRequest);
        }
    }
}
