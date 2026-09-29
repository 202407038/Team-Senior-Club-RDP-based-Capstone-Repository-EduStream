using EduStream.Core.Models;

namespace EduStream.FileTransfer.Tests;

public sealed class SeptemberWeek2RdpContractTests
{
    [Fact]
    public void NetworkFailure_RetryRejectsLateCallbackFromOldConnection()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var state = RdpConnectionStatus.Create(Guid.NewGuid(), "Student01")
            .BeginConnect(first).Connected(first).Fail(first, RdpFailureReason.NetworkInterrupted);
        Assert.True(state.CanRetry);
        var retry = state.BeginConnect(second);
        Assert.Equal(RdpConnectionState.Reconnecting, retry.State);
        Assert.Throws<InvalidOperationException>(() => retry.Connected(first));
        Assert.Equal(RdpConnectionState.Connected, retry.Connected(second).State);
    }

    [Theory]
    [InlineData(RdpFailureReason.AuthenticationFailed)]
    [InlineData(RdpFailureReason.AccessDenied)]
    [InlineData(RdpFailureReason.UnsupportedEnvironment)]
    [InlineData(RdpFailureReason.SessionClosed)]
    public void NonTransientFailure_DoesNotAutomaticallyRetry(RdpFailureReason reason)
    {
        var id = Guid.NewGuid();
        var state = RdpConnectionStatus.Create(Guid.NewGuid(), "Student01").BeginConnect(id).Fail(id, reason);
        Assert.False(state.CanRetry);
        Assert.Throws<InvalidOperationException>(() => state.BeginConnect(Guid.NewGuid()));
    }

    [Fact]
    public void ClosedSession_RejectsLateConnectAndRestart()
    {
        var id = Guid.NewGuid();
        var closed = RdpConnectionStatus.Create(Guid.NewGuid(), "Student01").BeginConnect(id).Close();
        Assert.Throws<InvalidOperationException>(() => closed.Connected(id));
        Assert.Throws<InvalidOperationException>(() => closed.BeginConnect(Guid.NewGuid()));
        Assert.Equal(closed, closed.Close());
    }

    [Fact]
    public void MissingIdentity_AndInvalidFailureAreRejected()
    {
        Assert.Throws<ArgumentException>(() => RdpConnectionStatus.Create(Guid.Empty, "student"));
        Assert.Throws<ArgumentException>(() => RdpConnectionStatus.Create(Guid.NewGuid(), " "));
        var idle = RdpConnectionStatus.Create(Guid.NewGuid(), "student");
        Assert.Throws<ArgumentException>(() => idle.BeginConnect(Guid.Empty));
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentOutOfRangeException>(() => idle.BeginConnect(id).Fail(id, (RdpFailureReason)999));
    }
}
