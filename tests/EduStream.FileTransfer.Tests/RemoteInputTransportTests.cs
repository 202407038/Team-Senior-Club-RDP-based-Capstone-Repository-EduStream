using EduStream.Core.Collaboration;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class RemoteInputTransportTests
{
    private sealed class Channel : ICollaborationChannel
    {
        public readonly List<RemoteInputCommandNotice> Commands = new();
        public Action<RemoteInputCommandNotice>? Sent;
        public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
        {
            var command = CollaborationMessageCodec.Decode<RemoteInputCommandNotice>(frame, out _);
            Commands.Add(command); Sent?.Invoke(command); return Task.CompletedTask;
        }
    }
    private static StudentRemoteInputGate CreateGate(
        Func<ParticipantConnection, ICollaborationChannel?> channel, Func<ParticipantConnection, Guid?> sharing,
        Func<ParticipantConnection, CancellationToken, Task<bool>>? disconnect = null) =>
        new("professor", new ParticipantRegistry(), channel, sharing, new EduStream.Core.Logging.InMemoryLogSink(),
            disconnectViewer: disconnect);
    private static RemoteControlState State()
    {
        var session = Guid.NewGuid();
        var professor = new ParticipantConnection(session, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var student = new ParticipantConnection(session, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
        return RemoteControlState.Request(professor, new ParticipantSnapshot(student, "Alice", true, true, true, 0), Guid.NewGuid());
    }
    private static RemoteInputResultNotice Result(RemoteInputCommandNotice command, bool applied = true) =>
        new(command.CommandId, command.SessionId, command.Action, applied, applied ? null : CollaborationError.PermissionDenied);

    [Fact]
    public async Task GrantDoesNotSucceedUntilMatchingStudentAcknowledges()
    {
        var state = State(); var channel = new Channel(); var sharing = Guid.NewGuid();
        var gate = CreateGate(_ => channel, _ => sharing);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var grant = gate.GrantAsync(state, stop.Token);
        var command = Assert.Single(channel.Commands);
        Assert.False(grant.IsCompleted);
        gate.HandleResult(state.Professor, Result(command));
        gate.HandleResult(state.Student, Result(command) with { CommandId = Guid.NewGuid() });
        gate.HandleResult(state.Student, Result(command) with { SessionId = Guid.NewGuid() });
        Assert.False(grant.IsCompleted);
        gate.HandleResult(state.Student, Result(command));
        await grant;
    }

    [Fact]
    public async Task NativeFailureIsNotReportedAsSuccess()
    {
        var state = State(); var channel = new Channel();
        var gate = CreateGate(_ => channel, _ => Guid.NewGuid());
        channel.Sent = command => gate.HandleResult(state.Student, Result(command, false));
        await Assert.ThrowsAsync<CollaborationException>(() => gate.GrantAsync(state, default));
    }

    [Fact]
    public async Task CancelledGrantStillRequiresRealRevokeAcknowledgement()
    {
        var state = State(); var channel = new Channel(); var sharing = Guid.NewGuid();
        var gate = CreateGate(_ => channel, _ => sharing);
        using var stop = new CancellationTokenSource();
        var grant = gate.GrantAsync(state, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grant);
        var revoke = gate.RevokeAsync(state.Revoke(), default);
        Assert.False(revoke.IsCompleted);
        Assert.Equal(RemoteInputAction.Revoke, channel.Commands[1].Action);
        Assert.Equal(sharing, channel.Commands[1].SharingId);
        gate.HandleResult(state.Student, Result(channel.Commands[0]));
        Assert.False(revoke.IsCompleted);
        gate.HandleResult(state.Student, Result(channel.Commands[1]));
        await revoke;
    }

    [Fact]
    public async Task MissingInvitationCannotGrant()
    {
        var gate = CreateGate(_ => new Channel(), _ => null);
        await Assert.ThrowsAsync<CollaborationException>(() => gate.GrantAsync(State(), default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingChannelRevokeRequiresConfirmedViewerDisconnect(bool confirmed)
    {
        var state = State(); var channel = new Channel(); var connected = true; var calls = 0;
        var gate = CreateGate(_ => connected ? channel : null, _ => Guid.NewGuid(),
            (student, _) => { Assert.Equal(state.Student, student); calls++; return Task.FromResult(confirmed); });
        channel.Sent = command => gate.HandleResult(state.Student, Result(command));
        await gate.GrantAsync(state, default);
        connected = false;
        if (confirmed) await gate.RevokeAsync(state.Revoke(), default);
        else await Assert.ThrowsAsync<CollaborationException>(() => gate.RevokeAsync(state.Revoke(), default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void InvalidOrIncompleteInputContractsAreRejected()
    {
        var state = State();
        var command = new RemoteInputCommandNotice(Guid.NewGuid(), state.Student.SessionId,
            Guid.NewGuid(), state.RequestId, ReverseRdpIdentity.For(state.Professor), RemoteInputAction.Grant);
        var bytes = CollaborationMessageCodec.Encode(Guid.NewGuid(), command);
        Assert.Equal(command, CollaborationMessageCodec.Decode<RemoteInputCommandNotice>(bytes, out _));
        Assert.ThrowsAny<Exception>(() => CollaborationMessageCodec.Encode(Guid.NewGuid(), command with { SharingId = Guid.Empty }));
        Assert.ThrowsAny<Exception>(() => CollaborationMessageCodec.Encode(Guid.NewGuid(), Result(command) with { SessionId = Guid.Empty }));
    }
}
