using EduStream.Core.Logging;
using EduStream.Core.Network;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public class RdpSharingServiceTests
{
    private readonly InMemoryLogSink _logSink;
    private readonly RdpSharingService _service;

    public RdpSharingServiceTests()
    {
        _logSink = new InMemoryLogSink();
        _service = new RdpSharingService(_logSink);
    }

    [Fact]
    public async Task StartAsync_CreatesNewSharingId()
    {
        var sessionId = Guid.NewGuid();
        var sharingId = await _service.StartAsync(sessionId);

        Assert.NotEqual(Guid.Empty, sharingId);
        Assert.Contains("공유 시작", string.Join("\n", _logSink.Snapshot()));
    }

    [Fact]
    public async Task StartAsync_ReturnsSameSharingIdWhenAlreadyStarted()
    {
        var sessionId = Guid.NewGuid();
        var firstSharingId = await _service.StartAsync(sessionId);
        var secondSharingId = await _service.StartAsync(sessionId);

        Assert.Equal(firstSharingId, secondSharingId);
    }

    [Fact]
    public async Task CreateInvitationAsync_CreatesInvitationPacket()
    {
        var sessionId = Guid.NewGuid();
        var sharingId = await _service.StartAsync(sessionId);
        var participantId = "student1";
        var connectionId = Guid.NewGuid();
        var invitationPassword = "test123";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var invitation = await _service.CreateInvitationAsync(
            sessionId, sharingId, participantId, connectionId, invitationPassword, expiresAt);

        Assert.NotNull(invitation);
        Assert.Equal(sessionId, invitation.SessionId);
        Assert.Equal(sharingId, invitation.SharingId);
        Assert.Equal(participantId, invitation.ParticipantId);
        Assert.Equal(connectionId, invitation.ConnectionId);
        Assert.Equal(1, invitation.ContractVersion);
        Assert.Equal("windows-desktop-sharing", invitation.Provider);
        Assert.True(invitation.ViewOnly);
        Assert.Contains("초대 생성", string.Join("\n", _logSink.Snapshot()));
    }

    [Fact]
    public async Task CreateInvitationAsync_ThrowsWhenNotStarted()
    {
        var sessionId = Guid.NewGuid();
        var sharingId = Guid.NewGuid();
        var participantId = "student1";
        var connectionId = Guid.NewGuid();
        var invitationPassword = "test123";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateInvitationAsync(sessionId, sharingId, participantId, connectionId, invitationPassword, expiresAt));
    }

    [Fact]
    public async Task CreateInvitationAsync_ThrowsWhenSharingIdMismatch()
    {
        var sessionId = Guid.NewGuid();
        var sharingId = await _service.StartAsync(sessionId);
        var wrongSharingId = Guid.NewGuid();
        var participantId = "student1";
        var connectionId = Guid.NewGuid();
        var invitationPassword = "test123";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateInvitationAsync(sessionId, wrongSharingId, participantId, connectionId, invitationPassword, expiresAt));
    }

    [Fact]
    public async Task RevokeInvitationAsync_DoesNotThrow()
    {
        var sessionId = Guid.NewGuid();
        var sharingId = await _service.StartAsync(sessionId);
        var participantId = "student1";
        var connectionId = Guid.NewGuid();
        var invitationPassword = "test123";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var invitation = await _service.CreateInvitationAsync(
            sessionId, sharingId, participantId, connectionId, invitationPassword, expiresAt);

        await _service.RevokeInvitationAsync(invitation.InvitationId);
        Assert.Contains("초대 폐기", string.Join("\n", _logSink.Snapshot()));
    }

    [Fact]
    public async Task StopAsync_StopsSharing()
    {
        var sessionId = Guid.NewGuid();
        await _service.StartAsync(sessionId);

        await _service.StopAsync();
        Assert.Contains("공유 종료", string.Join("\n", _logSink.Snapshot()));
    }

    [Fact]
    public async Task StopAsync_DoesNotThrowWhenNotStarted()
    {
        await _service.StopAsync();
    }

    [Fact]
    public async Task DisposeAsync_CallsStopAsync()
    {
        var sessionId = Guid.NewGuid();
        await _service.StartAsync(sessionId);

        await _service.DisposeAsync();
        Assert.Contains("공유 종료", string.Join("\n", _logSink.Snapshot()));
    }

    [Fact]
    public async Task CreateInvitationAsync_AllowsMultipleInvitations()
    {
        var sessionId = Guid.NewGuid();
        var sharingId = await _service.StartAsync(sessionId);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var invitation1 = await _service.CreateInvitationAsync(
            sessionId, sharingId, "student1", Guid.NewGuid(), "pass1", expiresAt);
        var invitation2 = await _service.CreateInvitationAsync(
            sessionId, sharingId, "student2", Guid.NewGuid(), "pass2", expiresAt);

        Assert.NotEqual(invitation1.InvitationId, invitation2.InvitationId);
        Assert.Contains("활성 초대=1/2", string.Join("\n", _logSink.Snapshot()));
        Assert.Contains("활성 초대=2/2", string.Join("\n", _logSink.Snapshot()));
    }

    [Fact]
    public async Task CreateInvitationAsync_ThrowsWhenMaxAttendeesExceeded()
    {
        var sessionId = Guid.NewGuid();
        var sharingId = await _service.StartAsync(sessionId);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        await _service.CreateInvitationAsync(sessionId, sharingId, "student1", Guid.NewGuid(), "pass1", expiresAt);
        await _service.CreateInvitationAsync(sessionId, sharingId, "student2", Guid.NewGuid(), "pass2", expiresAt);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateInvitationAsync(sessionId, sharingId, "student3", Guid.NewGuid(), "pass3", expiresAt));
    }

    [Fact]
    public async Task RevokeInvitationAsync_AllowsNewInvitationAfterRevocation()
    {
        var sessionId = Guid.NewGuid();
        var sharingId = await _service.StartAsync(sessionId);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var invitation1 = await _service.CreateInvitationAsync(
            sessionId, sharingId, "student1", Guid.NewGuid(), "pass1", expiresAt);
        var invitation2 = await _service.CreateInvitationAsync(
            sessionId, sharingId, "student2", Guid.NewGuid(), "pass2", expiresAt);

        await _service.RevokeInvitationAsync(invitation1.InvitationId);

        // 폐기 후 새로운 초대 가능
        var invitation3 = await _service.CreateInvitationAsync(
            sessionId, sharingId, "student3", Guid.NewGuid(), "pass3", expiresAt);

        Assert.NotEqual(invitation1.InvitationId, invitation3.InvitationId);
        Assert.Contains("초대 폐기", string.Join("\n", _logSink.Snapshot()));
    }
}
