using System.Text;
using EduStream.Core.Factories;
using EduStream.Core.Models;
namespace EduStream.FileTransfer.Tests;

public sealed class SeptemberWeek3FeatureIsolationTests
{
    [Fact]
    public void RdpFailure_DoesNotChangeFileStatusOrChatContract()
    {
        var session = Guid.NewGuid();
        var rdp = SeptemberContractTestSupport.ConnectedStudent(session, "Student01");
        var file = FeatureOperationResult.CreateStatus(FeatureArea.File, OperationState.InProgress,
            "파일 수신 중", 50, session);
        var chat = SeptemberContractTestSupport.RoundTrip(
            SeptemberContractTestSupport.StudentChat(session, "Student01", "한글 수신 확인"));
        var failed = rdp.Fail(rdp.ConnectionId, RdpFailureReason.NetworkInterrupted);
        Assert.True(failed.CanRetry);
        Assert.Equal(50, file.ProgressPercent);
        Assert.Equal(OperationState.InProgress, file.State);
        Assert.False(chat.IsSystemMessage);
        Assert.Equal(session, chat.SessionId);
        Assert.Equal(Encoding.UTF8.GetByteCount(chat.Message), chat.DataLength);
        Assert.Equal("Student01", chat.SenderId);
    }

    [Fact]
    public void SystemChat_RoundTripKeepsServerIdentityAndSession()
    {
        var session = Guid.NewGuid();
        var chat = SeptemberContractTestSupport.RoundTrip(PacketFactory.CreateSystemChat("RDP 공유 종료", session));
        Assert.True(chat.IsSystemMessage);
        Assert.Equal("system", chat.SenderId);
        Assert.Equal(session, chat.SessionId);
        Assert.Equal(Encoding.UTF8.GetByteCount(chat.Message), chat.DataLength);
    }
}
