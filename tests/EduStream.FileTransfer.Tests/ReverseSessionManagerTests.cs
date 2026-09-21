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
