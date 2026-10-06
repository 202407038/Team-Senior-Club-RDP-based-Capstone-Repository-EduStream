using System.Collections.Concurrent;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당: 학생 PC 실제 입력 허용/회수 게이트(Kind 18 송신·Kind 19 대기).
/// 현재 공유 세대가 없으면 허용하지 않고, 다른 연결·다른 동작의 응답, 시간 초과, 취소, 끊김, 종료를 성공으로 보지 않는지 확인합니다.
/// </summary>
public sealed class StudentRemoteInputGateTests
{
    private const string ProfessorId = "professor-id";

    [Fact]
    public async Task Grant_SendsCommandForCurrentSharing_AndCompletesOnAppliedResult()
    {
        using var rig = new Rig();
        var alice = rig.Join("alice");
        var sharing = rig.Share(alice);
        var state = rig.Request(alice);

        var grant = rig.Gate.GrantAsync(state, CancellationToken.None);
        var command = await rig.NextCommandAsync(alice);

        Assert.Equal(RemoteInputAction.Grant, command.Action);
        Assert.Equal(sharing, command.SharingId);
        Assert.Equal(state.RequestId, command.ControlRequestId);
        Assert.Equal(ProfessorId, command.ProfessorId);
        Assert.Equal(alice.SessionId, command.SessionId);
        Assert.False(grant.IsCompleted);

        Assert.True(rig.Gate.HandleResult(alice, Applied(command)));
        await grant;
        Assert.Equal(0, rig.Gate.PendingCount);
    }

    [Fact]
    public async Task Grant_WithoutViewableSharing_IsRejectedWithoutSending()
    {
        using var rig = new Rig();
        var alice = rig.Join("alice");

        var ex = await Assert.ThrowsAsync<CollaborationException>(() => rig.Gate.GrantAsync(rig.Request(alice), CancellationToken.None));

        Assert.Equal(CollaborationError.StaleConnection, ex.Code);
        Assert.Empty(rig.Channel(alice).Frames);
    }

    [Fact]
    public async Task Grant_FailedResult_StillRequiresConfirmedRevokeForPossiblePartialApply()
    {
        using var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Share(alice);
        var state = rig.Request(alice);

        var grant = rig.Gate.GrantAsync(state, CancellationToken.None);
        var command = await rig.NextCommandAsync(alice);
        rig.Gate.HandleResult(alice, new RemoteInputResultNotice(command.CommandId, command.SessionId,
            RemoteInputAction.Grant, false, CollaborationError.UnsupportedCapability));

        Assert.Equal(CollaborationError.UnsupportedCapability, (await Assert.ThrowsAsync<CollaborationException>(() => grant)).Code);
        var revoke = rig.Gate.RevokeAsync(state.Revoke(), CancellationToken.None);
        var revokeCommand = (await rig.CommandsAsync(alice, 2))[1];
        Assert.False(revoke.IsCompleted);
        rig.Gate.HandleResult(alice, Applied(revokeCommand));
        await revoke;
    }

    [Fact]
    public async Task ResultFromOtherStudentOrWrongAction_IsIgnored()
    {
        using var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Share(alice);

        var grant = rig.Gate.GrantAsync(rig.Request(alice), CancellationToken.None);
        var command = await rig.NextCommandAsync(alice);

        Assert.False(rig.Gate.HandleResult(bob, Applied(command)));
        Assert.False(rig.Gate.HandleResult(alice, Applied(command) with { Action = RemoteInputAction.Revoke }));
        Assert.False(rig.Gate.HandleResult(alice, Applied(command) with { SessionId = Guid.NewGuid() }));
        Assert.False(rig.Gate.HandleResult(alice, Applied(command) with { CommandId = Guid.NewGuid() }));
        Assert.False(grant.IsCompleted);

        Assert.True(rig.Gate.HandleResult(alice, Applied(command)));
        await grant;
        // 이미 끝난 명령의 중복 응답은 받지 않는다.
        Assert.False(rig.Gate.HandleResult(alice, Applied(command)));
    }

    [Fact]
    public async Task GrantTimeout_Fails_AndRevokeTargetsSameSharing()
    {
        using var rig = new Rig(TimeSpan.FromMilliseconds(100));
        var alice = rig.Join("alice");
        var sharing = rig.Share(alice);
        var state = rig.Request(alice);

        await Assert.ThrowsAsync<TimeoutException>(() => rig.Gate.GrantAsync(state, CancellationToken.None));

        // 응답만 늦었을 수 있으므로 학생이 공유를 바꿨더라도 허용을 보낸 세대로 회수한다.
        rig.Share(alice);
        var revoke = rig.Gate.RevokeAsync(state.Revoke(), CancellationToken.None);
        var commands = await rig.CommandsAsync(alice, 2);
        Assert.Equal(RemoteInputAction.Revoke, commands[1].Action);
        Assert.Equal(sharing, commands[1].SharingId);
        rig.Gate.HandleResult(alice, Applied(commands[1]));
        await revoke;
    }

    [Fact]
    public async Task GrantCancelled_ThrowsCancellation_AndRevokeIsStillSent()
    {
        using var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Share(alice);
        var state = rig.Request(alice);
        using var cancellation = new CancellationTokenSource();

        var grant = rig.Gate.GrantAsync(state, cancellation.Token);
        await rig.NextCommandAsync(alice);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grant);
        var revoke = rig.Gate.RevokeAsync(state.Revoke(), CancellationToken.None);
        var commands = await rig.CommandsAsync(alice, 2);
        Assert.Equal(RemoteInputAction.Revoke, commands[1].Action);
        rig.Gate.HandleResult(alice, Applied(commands[1]));
        await revoke;
    }

    [Fact]
    public async Task RevokeFailedResult_Throws_SoCoordinatorRetries()
    {
        using var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Share(alice);
        var state = rig.Request(alice);
        var grant = rig.Gate.GrantAsync(state, CancellationToken.None);
        rig.Gate.HandleResult(alice, Applied(await rig.NextCommandAsync(alice)));
        await grant;

        var revoke = rig.Gate.RevokeAsync(state.Revoke(), CancellationToken.None);
        var failed = (await rig.CommandsAsync(alice, 2))[1];
        rig.Gate.HandleResult(alice, new RemoteInputResultNotice(failed.CommandId, failed.SessionId,
            RemoteInputAction.Revoke, false, CollaborationError.PermissionDenied));
        await Assert.ThrowsAsync<CollaborationException>(() => revoke);

        // 재시도는 같은 세대로 다시 보낸다.
        var retry = rig.Gate.RevokeAsync(state.Revoke(), CancellationToken.None);
        var again = (await rig.CommandsAsync(alice, 3))[2];
        Assert.Equal(failed.SharingId, again.SharingId);
        rig.Gate.HandleResult(alice, Applied(again));
        await retry;
    }

    [Fact]
    public async Task RevokeWithoutGrant_CompletesWithoutSending()
    {
        using var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Share(alice);

        await rig.Gate.RevokeAsync(rig.Request(alice).Revoke(), CancellationToken.None);

        Assert.Empty(rig.Channel(alice).Frames);
    }

    [Fact]
    public async Task StudentDisconnected_DoesNotInventAppliedRevokeResult()
    {
        using var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Share(alice);
        rig.Share(bob);
        var aliceState = rig.Request(alice);
        var bobState = rig.Request(bob);

        var aliceGrant = rig.Gate.GrantAsync(aliceState, CancellationToken.None);
        await rig.NextCommandAsync(alice);
        var bobGrant = rig.Gate.GrantAsync(bobState, CancellationToken.None);
        rig.Gate.HandleResult(bob, Applied(await rig.NextCommandAsync(bob)));
        await bobGrant;
        var bobRevoke = rig.Gate.RevokeAsync(bobState.Revoke(), CancellationToken.None);
        await rig.CommandsAsync(bob, 2);

        rig.Disconnect("alice");
        rig.Disconnect("bob");

        Assert.Equal(CollaborationError.StaleConnection, (await Assert.ThrowsAsync<CollaborationException>(() => aliceGrant)).Code);
        await Assert.ThrowsAsync<CollaborationException>(() => bobRevoke);
        // 보호 채널과 WDS 연결은 별개다. 닫힘 확인이 없으면 이후 회수도 실패를 유지한다.
        await Assert.ThrowsAsync<CollaborationException>(() => rig.Gate.RevokeAsync(aliceState.Revoke(), CancellationToken.None));
        Assert.Single(rig.Channel(alice).Frames);
    }

    [Fact]
    public async Task Dispose_FailsPendingAndRejectsNewGrant()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Share(alice);
        var grant = rig.Gate.GrantAsync(rig.Request(alice), CancellationToken.None);
        await rig.NextCommandAsync(alice);

        rig.Dispose();

        Assert.Equal(CollaborationError.SessionClosed, (await Assert.ThrowsAsync<CollaborationException>(() => grant)).Code);
        Assert.Equal(CollaborationError.SessionClosed, (await Assert.ThrowsAsync<CollaborationException>(
            () => rig.Gate.GrantAsync(rig.Request(alice), CancellationToken.None))).Code);
    }

    private static RemoteInputResultNotice Applied(RemoteInputCommandNotice command) =>
        new(command.CommandId, command.SessionId, command.Action, true, null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisconnectedRevoke_RequiresActualViewerCloseConfirmation(bool closed)
    {
        var closeCalls = 0;
        using var rig = new Rig(disconnectViewer: (_, _) => { closeCalls++; return Task.FromResult(closed); });
        var alice = rig.Join("alice");
        rig.Share(alice);
        var state = rig.Request(alice);
        var grant = rig.Gate.GrantAsync(state, CancellationToken.None);
        rig.Gate.HandleResult(alice, Applied(await rig.NextCommandAsync(alice)));
        await grant;
        rig.Disconnect("alice");
        if (closed)
        {
            await rig.Gate.RevokeAsync(state.Revoke(), CancellationToken.None);
            await rig.Gate.RevokeAsync(state.Revoke(), CancellationToken.None);
            Assert.Equal(1, closeCalls);
        }
        else
        {
            await Assert.ThrowsAsync<CollaborationException>(() => rig.Gate.RevokeAsync(state.Revoke(), CancellationToken.None));
            await Assert.ThrowsAsync<CollaborationException>(() => rig.Gate.RevokeAsync(state.Revoke(), CancellationToken.None));
            Assert.Equal(2, closeCalls); // 실패한 회수는 다음 시도까지 기록을 유지한다.
        }
    }

    [Fact]
    public async Task RevokeTimeout_ClosesViewerOnlyAfterAckTimeout()
    {
        var closed = false;
        using var rig = new Rig(TimeSpan.FromMilliseconds(100), (_, _) => { closed = true; return Task.FromResult(true); });
        var alice = rig.Join("alice");
        rig.Share(alice);
        var state = rig.Request(alice);
        var grant = rig.Gate.GrantAsync(state, CancellationToken.None);
        rig.Gate.HandleResult(alice, Applied(await rig.NextCommandAsync(alice)));
        await grant;
        var revoke = rig.Gate.RevokeAsync(state.Revoke(), CancellationToken.None);
        Assert.False(closed);
        await revoke;
        Assert.True(closed);
    }

    private sealed class Rig : IDisposable
    {
        private readonly ConcurrentDictionary<Guid, FakeChannel> _channels = new();
        private readonly ConcurrentDictionary<Guid, Guid> _sharings = new();

        public Rig(TimeSpan? timeout = null, Func<ParticipantConnection, CancellationToken, Task<bool>>? disconnectViewer = null)
        {
            Professor = new ParticipantConnection(SessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
            Gate = new StudentRemoteInputGate(ProfessorId, Registry,
                student => Registry.TryResolve(student.ConnectionId) is null ? null : Channel(student),
                student => _sharings.TryGetValue(student.ConnectionId, out var sharing) ? sharing : null,
                new InMemoryLogSink(), timeout, disconnectViewer);
        }

        public Guid SessionId { get; } = Guid.NewGuid();
        public ParticipantRegistry Registry { get; } = new();
        public ParticipantConnection Professor { get; }
        public StudentRemoteInputGate Gate { get; }

        public ParticipantConnection Join(string clientId) =>
            Registry.Join(clientId, SessionId, clientId, ParticipantRole.Student);

        public void Disconnect(string clientId) => Registry.Disconnect(clientId);

        public Guid Share(ParticipantConnection student) => _sharings[student.ConnectionId] = Guid.NewGuid();

        public RemoteControlState Request(ParticipantConnection student) =>
            RemoteControlState.Request(Professor, Registry.TryResolve(student.ConnectionId)!, Guid.NewGuid());

        public FakeChannel Channel(ParticipantConnection student) => _channels.GetOrAdd(student.ConnectionId, _ => new FakeChannel());

        public async Task<RemoteInputCommandNotice> NextCommandAsync(ParticipantConnection student) =>
            (await CommandsAsync(student, 1))[^1];

        public async Task<RemoteInputCommandNotice[]> CommandsAsync(ParticipantConnection student, int count)
        {
            var channel = Channel(student);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (channel.Frames.Count < count)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("명령이 전송되지 않았습니다.");
                await Task.Delay(10);
            }
            return channel.Frames.Select(frame => CollaborationMessageCodec.Decode<RemoteInputCommandNotice>(frame, out _)).ToArray();
        }

        public void Dispose() => Gate.Dispose();
    }

    private sealed class FakeChannel : ICollaborationChannel
    {
        public ConcurrentQueue<byte[]> Frames { get; } = new();

        public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
        {
            Frames.Enqueue(frame);
            return Task.CompletedTask;
        }
    }
}
