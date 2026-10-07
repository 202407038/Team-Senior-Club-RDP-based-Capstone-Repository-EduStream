using EduStream.Server;

namespace EduStream.FileTransfer.Tests;

public sealed class StudentViewerRetryPolicyTests
{
    [Theory]
    [InlineData(true, false, true, false, "Refresh")]
    [InlineData(true, false, true, true, "Refresh")]
    [InlineData(false, true, false, false, "KeepConnecting")]
    [InlineData(false, false, false, false, "Connect")]
    [InlineData(false, false, true, false, "WaitForInvitation")]
    [InlineData(false, false, false, true, "WaitForInvitation")]
    public void RetryDependsOnActualConnectionNotJustAttempt(bool live, bool connecting,
        bool established, bool expired, string expected) =>
        Assert.Equal(expected, StudentViewerRetryPolicy.Decide(live, connecting, established, expired).ToString());
}
