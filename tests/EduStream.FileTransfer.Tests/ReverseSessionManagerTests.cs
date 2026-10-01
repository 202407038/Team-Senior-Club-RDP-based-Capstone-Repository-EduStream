using System;
using System.Reflection;
using System.Threading.Tasks;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;


public class ReverseSessionManagerTests
{
    private readonly ReverseSessionManager _manager = new();

    // 이제 [Fact] 대신 [WdsFact]만 달아두면 실행 전에 알아서 환경을 검사하고 Skip을 때립니다!
    [WdsFact]
    public async Task StartReverseSharingAsync_처음_시작시_성공()
    {
        var sessionId = Guid.NewGuid();
        var studentId = "student1";

        var sharingId = await _manager.StartReverseSharingAsync(sessionId, studentId);

        Assert.NotEqual(Guid.Empty, sharingId);
        Assert.True(_manager.IsReverseSharingActive);
    }

    [WdsFact]
    public async Task StartReverseSharingAsync_이미_활성화된_경우_예외_발생()
    {
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.StartReverseSharingAsync(sessionId, "student2"));
    }

    [WdsFact]
    public async Task CreateProfessorInvitationAsync_호스팅_상태에서_성공()
    {
        var sessionId = Guid.NewGuid();
        var studentId = "student1";
        var sharingId = await _manager.StartReverseSharingAsync(sessionId, studentId);

        var invitation = await _manager.CreateProfessorInvitationAsync(
            sessionId,
            sharingId,
            "professor1",
            Guid.NewGuid(),
            "password123",
            DateTimeOffset.UtcNow.AddHours(1)
        );

        Assert.NotNull(invitation);
        Assert.Equal(sessionId, invitation.SessionId);
        Assert.Equal(sharingId, invitation.SharingId);
        Assert.Equal("professor1", invitation.ProfessorId);
        Assert.Equal(studentId, invitation.HostStudentId);
        Assert.Equal(ReverseSessionState.Hosting, _manager.CurrentState);
    }

    [WdsFact]
    public async Task ConnectAsync_호스팅_상태에서_연결_중으로_전이()
    {
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");

        await _manager.ConnectAsync();

        Assert.Equal(ReverseSessionState.Connecting, _manager.CurrentState);
    }

    [WdsFact]
    public async Task OnConnectedAsync_참석자없음_예외_및_실패상태_전이()
    {
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");
        await _manager.ConnectAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.OnConnectedAsync());
        Assert.Contains("접속한 참석자가 없어", ex.Message);
        Assert.Equal(ReverseSessionState.Failed, _manager.CurrentState);
    }

    [WdsFact]
    public async Task OnConnectionFailedAsync_실패_상태_전이()
    {
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");
        await _manager.ConnectAsync();

        await _manager.OnConnectionFailedAsync();

        Assert.Equal(ReverseSessionState.Failed, _manager.CurrentState);
    }

    [WdsFact]
    public async Task OnDisconnectedAsync_종료_상태_전이()
    {
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");
        await _manager.ConnectAsync();

        await _manager.OnDisconnectedAsync();

        Assert.Equal(ReverseSessionState.Disconnected, _manager.CurrentState);
    }

    // 이 두 가지는 엔진 유무와 관계없이 통신 이벤트 로직만 검증하므로 일반 [Fact] 유지
    [Fact]
    public void ReceiveFrame_ControlGranted_상태에서_이벤트_발생()
    {
        var stateField = typeof(ReverseSessionManager).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
        stateField?.SetValue(_manager, ReverseSessionState.ControlGranted);

        FrameReceivedEventArgs? receivedArgs = null;
        _manager.FrameReceived += (sender, args) => receivedArgs = args;

        var frameData = new byte[] { 0x01, 0x02, 0x03 };

        _manager.ReceiveFrame(frameData);

        Assert.NotNull(receivedArgs);
        Assert.Equal(frameData, receivedArgs.FrameData);
    }

    [Fact]
    public void ReceiveFrame_비연결_상태에서_이벤트_무시()
    {
        FrameReceivedEventArgs? receivedArgs = null;
        _manager.FrameReceived += (sender, args) => receivedArgs = args;

        var frameData = new byte[] { 0x01, 0x02, 0x03 };

        _manager.ReceiveFrame(frameData);

        Assert.Null(receivedArgs);
    }

    [WdsFact]
    public async Task CreateProfessorInvitationAsync_비활성_상태에서_예외_발생()
    {
        var sessionId = Guid.NewGuid();
        var sharingId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.CreateProfessorInvitationAsync(
                sessionId,
                sharingId,
                "professor1",
                Guid.NewGuid(),
                "password123",
                DateTimeOffset.UtcNow.AddHours(1)
            ));
    }

    [WdsFact]
    public async Task StopReverseSharingAsync_활성화_해제()
    {
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");

        await _manager.StopReverseSharingAsync();

        Assert.False(_manager.IsReverseSharingActive);
    }
}