using EduStream.Core.Logging;
using EduStream.Core.Network;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public class RdpSharingServiceTests : IAsyncLifetime
{
    private readonly InMemoryLogSink _logSink;
    private readonly RdpSharingService _service;
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _service.DisposeAsync().AsTask();

    public RdpSharingServiceTests()
    {
        _logSink = new InMemoryLogSink();
        // 테스트 대역: 모의 RDPSession 객체 사용
        _service = new RdpSharingService(_logSink, () => new MockRdpSession());
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
    public async Task InvalidMonitorSetupClosesOpenedNativeSession()
    {
        var native = new MonitorFailureSession();
        await using var service = new RdpSharingService(_logSink, () => native)
        { SelectedBounds = new System.Drawing.Rectangle(0, 0, 1920, 1080) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(Guid.NewGuid()));
        Assert.True(native.Opened);
        Assert.True(native.Closed);
        Assert.Contains(_logSink.Snapshot(), entry => entry.Contains("모니터 영역 설정 실패"));
    }

    private sealed class MonitorFailureSession
    {
        public bool Opened { get; private set; }
        public bool Closed { get; private set; }
        public int ColorDepth { get; set; }
        public void Open() => Opened = true;
        public void Close() => Closed = true;
        public void SetDesktopSharedRect(int left, int top, int right, int bottom) => throw new InvalidOperationException("monitor failure");
    }

    [Fact]
    public async Task StartAsync_ThrowsWhenAlreadyStarted()
    {
        var sessionId = Guid.NewGuid();
        await _service.StartAsync(sessionId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.StartAsync(sessionId));
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

        // 🎯 [정상 복구] 기본 정방향(교수->학생) 공유이므로 반드시 ViewOnly가 true여야 합니다.
        Assert.True(invitation.ViewOnly);

        Assert.Contains("초대 생성", string.Join("\n", _logSink.Snapshot()));
        Assert.DoesNotContain("password", invitation.ConnectionString);
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
    public async Task CreateInvitationAsync_IncludesDataLength()
    {
        var sessionId = Guid.NewGuid();
        var sharingId = await _service.StartAsync(sessionId);
        var participantId = "student1";
        var connectionId = Guid.NewGuid();
        var invitationPassword = "test123";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var invitation = await _service.CreateInvitationAsync(
            sessionId, sharingId, participantId, connectionId, invitationPassword, expiresAt);

        Assert.True(invitation.DataLength > 0);
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

    [Fact]
    public async Task StopAsync_AllowsRestartWithNewSharingId()
    {
        var sessionId = Guid.NewGuid();
        var firstSharingId = await _service.StartAsync(sessionId);
        await _service.StopAsync();

        var secondSharingId = await _service.StartAsync(sessionId);
        Assert.NotEqual(firstSharingId, secondSharingId);
    }
}

/// <summary>
/// 테스트 대역용 모의 RDPSession 객체
/// </summary>
internal class MockRdpSession
{
    // 🎯 [피드백 5번 연동 에러 방지] 깡통 객체에 ColorDepth 속성을 달아줘서 예외가 터지지 않게 합니다.
    public int ColorDepth { get; set; } = 24;

    public void Open() { }
    public void Close() { }
    public MockInvitations Invitations { get; } = new MockInvitations();
}

internal class MockInvitations
{
    // 기존 서비스의 CreateInvitation 인자가 4개로 고정되었으므로 이 깡통도 4개를 받도록 맞춥니다.
    public MockInvitation CreateInvitation(string authString, string groupName, string password, int attendeeLimit)
    {
        return new MockInvitation();
    }
}

internal class MockInvitation
{
    public string ConnectionString => $"rdp://mock-invitation:{Guid.NewGuid()}";
    public bool Revoked { get; set; }
}
