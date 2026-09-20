using EduStream.ContractTestDoubles;
using EduStream.Core.Collaboration;

namespace EduStream.FileTransfer.Tests;

public sealed class FinalReadinessTestDoubleTests
{
    [Fact]
    public async Task RoomDouble_SupportsExplicitFailureAndPermissionUi()
    {
        var client = new SimulatedRoomClient { JoinFailure = CollaborationError.NotAuthorized };
        var request = new RoomJoinRequest("localhost", 5000, "학생", Guid.NewGuid());
        await Assert.ThrowsAsync<CollaborationException>(() => client.JoinAsync(request, ReadOnlyMemory<char>.Empty));
        Assert.Null(client.Current);
        client.JoinFailure = null;
        var joined = await client.JoinAsync(request, ReadOnlyMemory<char>.Empty);
        Assert.True(client.IsSimulation);
        Assert.True(joined.Participants.Single().AllowControl);
        await client.SetPermissionsAsync(false, true);
        Assert.False(client.Current!.Participants.Single().AllowControl);
        Assert.False(client.Current.Participants.Single().AllowViewing);
        await client.LeaveAsync();
        Assert.Null(client.Current);
    }

    [Fact]
    public async Task RemoteDouble_RequiresExplicitConfirmationAndRevokesOldTarget()
    {
        var session = Guid.NewGuid();
        var professor = new ParticipantConnection(session, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Professor);
        var a = new ParticipantSnapshot(new(session, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student),
            "학생 1", true, true, true, 1);
        var b = a with { Connection = a.Connection with { ParticipantId = Guid.NewGuid(), ConnectionId = Guid.NewGuid() } };
        var changes = new List<RemoteControlState>();
        var control = new SimulatedRemoteControl(professor, c => c == a.Connection ? a : b);
        control.StateChanged += changes.Add;
        await control.RequestAsync(a.Connection);
        Assert.Equal(ControlPhase.Requested, control.Current!.Phase);
        control.ConfirmNativeControlForTest();
        await control.RequestAsync(b.Connection);
        Assert.Equal(ControlPhase.Revoked, changes[^2].Phase);
        Assert.Equal(a.Connection, changes[^2].Student);
        Assert.Equal(ControlPhase.Requested, changes[^1].Phase);
        await control.StopAsync();
        Assert.Throws<CollaborationException>(control.ConfirmNativeControlForTest);
    }

    [Fact]
    public void Errors_AreSanitizedAndAllDomainValuesMapped()
    {
        foreach (var code in Enum.GetValues<CollaborationError>())
            Assert.NotEqual("INTERNAL_ERROR", CollaborationErrorCatalog.Describe(code).Code);
        var path = @"C:\Private\secret.txt";
        Assert.DoesNotContain(path, CollaborationErrorCatalog.FromException(new IOException(path)).ToString());
        Assert.True(CollaborationErrorCatalog.FromException(new OperationCanceledException()).CanRetry);
        Assert.False(CollaborationErrorCatalog.Describe(CollaborationError.PermissionDenied).CanRetry);
    }
}
