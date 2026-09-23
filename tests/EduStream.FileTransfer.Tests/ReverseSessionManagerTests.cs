using System;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

public class ReverseSessionManagerTests
{
    private readonly ReverseSessionManager _manager = new();

    [Fact]
    public async Task StartReverseSharingAsync_처음_시작시_성공()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var studentId = "student1";

        // Act
        var sharingId = await _manager.StartReverseSharingAsync(sessionId, studentId);

        // Assert
        Assert.NotEqual(Guid.Empty, sharingId);
        Assert.True(_manager.IsReverseSharingActive);
    }

    [Fact]
    public async Task StartReverseSharingAsync_이미_활성화된_경우_예외_발생()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.StartReverseSharingAsync(sessionId, "student2"));
    }

    [Fact]
    public async Task CreateProfessorInvitationAsync_호스팅_상태에서_성공()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var studentId = "student1";
        var sharingId = await _manager.StartReverseSharingAsync(sessionId, studentId);

        // Act
        var invitation = await _manager.CreateProfessorInvitationAsync(
            sessionId,
            sharingId,
            "professor1",
            Guid.NewGuid(),
            "password123",
            DateTimeOffset.UtcNow.AddHours(1)
        );

        // Assert
        Assert.NotNull(invitation);
        Assert.Equal(sessionId, invitation.SessionId);
        Assert.Equal(sharingId, invitation.SharingId);
        Assert.Equal("professor1", invitation.ProfessorId);
        Assert.Equal(studentId, invitation.HostStudentId);
        Assert.Equal(ReverseSessionState.Hosting, _manager.CurrentState);
    }

    [Fact]
    public async Task ConnectAsync_호스팅_상태에서_연결_중으로_전이()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");

        // Act
        await _manager.ConnectAsync();

        // Assert
        Assert.Equal(ReverseSessionState.Connecting, _manager.CurrentState);
    }

    [Fact]
    public async Task OnConnectedAsync_연결_성공_상태_전이()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");
        await _manager.ConnectAsync();

        // Act
        await _manager.OnConnectedAsync();

        // Assert
        Assert.Equal(ReverseSessionState.Connected, _manager.CurrentState);
    }

    [Fact]
    public async Task OnConnectionFailedAsync_실패_상태_전이()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");
        await _manager.ConnectAsync();

        // Act
        await _manager.OnConnectionFailedAsync();

        // Assert
        Assert.Equal(ReverseSessionState.Failed, _manager.CurrentState);
    }

    [Fact]
    public async Task OnDisconnectedAsync_종료_상태_전이()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");
        await _manager.ConnectAsync();
        await _manager.OnConnectedAsync();

        // Act
        await _manager.OnDisconnectedAsync();

        // Assert
        Assert.Equal(ReverseSessionState.Disconnected, _manager.CurrentState);
    }

    [Fact]
    public async Task ReceiveFrame_Connected_상태에서_이벤트_발생()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");
        await _manager.ConnectAsync();
        await _manager.OnConnectedAsync();

        FrameReceivedEventArgs? receivedArgs = null;
        _manager.FrameReceived += (sender, args) => receivedArgs = args;

        var frameData = new byte[] { 0x01, 0x02, 0x03 };

        // Act
        _manager.ReceiveFrame(frameData);

        // Assert
        Assert.NotNull(receivedArgs);
        Assert.Equal(frameData, receivedArgs.FrameData);
    }

    [Fact]
    public async Task ReceiveFrame_비연결_상태에서_이벤트_무시()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");

        FrameReceivedEventArgs? receivedArgs = null;
        _manager.FrameReceived += (sender, args) => receivedArgs = args;

        var frameData = new byte[] { 0x01, 0x02, 0x03 };

        // Act
        _manager.ReceiveFrame(frameData);

        // Assert
        Assert.Null(receivedArgs);
    }

    [Fact]
    public async Task CreateProfessorInvitationAsync_비활성_상태에서_예외_발생()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var sharingId = Guid.NewGuid();

        // Act & Assert
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

    [Fact]
    public async Task StopReverseSharingAsync_활성화_해제()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        await _manager.StartReverseSharingAsync(sessionId, "student1");

        // Act
        await _manager.StopReverseSharingAsync();

        // Assert
        Assert.False(_manager.IsReverseSharingActive);
    }
}
