using EduStream.Core.Models;
namespace EduStream.FileTransfer.Tests;

public sealed class SeptemberWeek4RdpRegressionTests
{
    [Fact]
    public void OneStudentDisconnect_LeavesOtherStudentContractConnected()
    {
        var session = Guid.NewGuid();
        var first = SeptemberContractTestSupport.ConnectedStudent(session, "Student01");
        var second = SeptemberContractTestSupport.ConnectedStudent(session, "Student02");
        var closed = first.Close();
        Assert.Equal(RdpConnectionState.Closed, closed.State);
        Assert.Equal(RdpConnectionState.Connected, second.State);
        Assert.Equal(session, second.SessionId);
        Assert.NotEqual(first.ConnectionId, second.ConnectionId);
    }

    [Fact]
    public void RepeatedRetry_RequiresFreshIdentity_AndKeepsSession()
    {
        var state = SeptemberContractTestSupport.ConnectedStudent(Guid.NewGuid(), "Student01");
        var session = state.SessionId;
        for (var i = 0; i < 20; i++)
        {
            var oldId = state.ConnectionId;
            state = state.Fail(oldId, RdpFailureReason.HostUnavailable);
            Assert.Throws<ArgumentException>(() => state.BeginConnect(oldId));
            state = state.BeginConnect(Guid.NewGuid());
            state = state.Connected(state.ConnectionId);
            Assert.Equal(session, state.SessionId);
        }
    }
}
