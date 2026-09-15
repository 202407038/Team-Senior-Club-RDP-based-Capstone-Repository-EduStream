using EduStream.Core.Logging;
using EduStream.Core.Utils;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

[Collection("WDS integration")]
public sealed class RdpSharingContractTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public RdpSharingContractTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    [WdsTheory]
    [InlineData("ReviewStudent1")]
    [InlineData("ReviewStudent3")]
    public async Task RealSharingTwoInvitationsRevokeReissueAndRestart(string replacementStudent)
    {
        var log = new InMemoryLogSink();
        await using var service = new RdpSharingService(log);
        var session = Guid.NewGuid();
        var sharing = await service.StartAsync(session);
        _output.WriteLine("START_PASS");
        var connection = Guid.NewGuid();
        var first = await service.CreateInvitationAsync(session, sharing, "ReviewStudent1", connection,
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(1));
        RdpInvitationContract.Validate(first, session, "ReviewStudent1", connection, DateTimeOffset.UtcNow);
        _output.WriteLine("INVITATION_1_VALID_PASS");
        var secondConnection = Guid.NewGuid();
        var second = await service.CreateInvitationAsync(session, sharing, "ReviewStudent2", secondConnection,
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(1));
        RdpInvitationContract.Validate(second, session, "ReviewStudent2", secondConnection, DateTimeOffset.UtcNow);
        _output.WriteLine("INVITATION_2_VALID_PASS");
        await service.RevokeInvitationAsync(first.InvitationId);
        _output.WriteLine("REVOKE_1_RETURNED");
        var newConnection = Guid.NewGuid();
        var replacement = await service.CreateInvitationAsync(session, sharing, replacementStudent, newConnection,
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(1));
        RdpInvitationContract.Validate(replacement, session, replacementStudent, newConnection, DateTimeOffset.UtcNow);
        _output.WriteLine("SAME_STUDENT_REISSUE_PASS");
        Assert.NotEqual(first.ConnectionString, replacement.ConnectionString);
        await service.StopAsync();
        _output.WriteLine("STOP_PASS");
        var newSharing = await service.StartAsync(Guid.NewGuid());
        Assert.NotEqual(sharing, newSharing);
        await service.StopAsync();
    }

    [Fact]
    public async Task MustUseSuppliedPassword()
    {
        var mock = new PasswordSession();
        await using var service = new RdpSharingService(new InMemoryLogSink(), () => mock);
        var session = Guid.NewGuid();
        var sharing = await service.StartAsync(session);
        const string password = "review-only-not-a-real-secret";
        await service.CreateInvitationAsync(session, sharing, "ReviewOnly", Guid.NewGuid(), password,
            DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(mock.Invitations.UsedPassword == password, "Caller-supplied password must be preserved.");
    }

    public sealed class PasswordSession
    {
        public System.Windows.Threading.Dispatcher? Dispatcher { get; private set; }
        public void Open() { Dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher; }
        public void Close() { }
        public PasswordInvitations Invitations { get; } = new();
    }
    public sealed class PasswordInvitations
    {
        public List<PasswordInvitation> Created { get; } = new();
        public string? UsedPassword { get; private set; }
        public PasswordInvitation CreateInvitation(string group, string auth, string password, int limit)
        {
            UsedPassword = password;
            var invitation = new PasswordInvitation { GroupName = auth };
            Created.Add(invitation);
            return invitation;
        }
    }
    public sealed class PasswordInvitation
    {
        public string GroupName { get; set; } = string.Empty;
        public string ConnectionString => "review-only-provider-string";
        public bool Revoked { get; set; }
    }

    [Fact]
    public async Task AttendeePolicy_ShouldUseInvitationNotDisplayName_AndRevokeOnlyTarget()
    {
        var mock = new PasswordSession();
        await using var service = new RdpSharingService(new InMemoryLogSink(), () => mock);
        var session = Guid.NewGuid();
        var sharing = await service.StartAsync(session);
        var first = await service.CreateInvitationAsync(session, sharing, "Alice", Guid.NewGuid(), "secret", DateTimeOffset.UtcNow.AddMinutes(1));
        await service.CreateInvitationAsync(session, sharing, "Bob", Guid.NewGuid(), "secret", DateTimeOffset.UtcNow.AddMinutes(1));
        var alice = new FakeAttendee { Id = 1, RemoteName = "Spoofed", Invitation = mock.Invitations.Created[0] };
        var bob = new FakeAttendee { Id = 2, RemoteName = "Spoofed", Invitation = mock.Invitations.Created[1] };
        var unknown = new FakeAttendee { Id = 3, RemoteName = "Alice", Invitation = new() { GroupName = "unknown" } };
        await mock.Dispatcher!.InvokeAsync(() =>
        {
            Invoke(service, "OnConnected", alice);
            Invoke(service, "OnConnected", bob);
            Invoke(service, "OnConnected", unknown);
            Invoke(service, "OnControlRequested", alice, 3);
        });
        Assert.Equal(2, alice.ControlLevel);
        Assert.Equal(2, bob.ControlLevel);
        Assert.True(unknown.Terminated);
        await service.RevokeInvitationAsync(first.InvitationId);
        Assert.True(alice.Terminated);
        Assert.False(bob.Terminated);
        Assert.True(mock.Invitations.Created[0].Revoked);
        await service.RevokeInvitationAsync(first.InvitationId); // 반복 해제 허용
    }

    private static void Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(target, args);

    public sealed class FakeAttendee
    {
        public int Id { get; init; }
        public string RemoteName { get; init; } = string.Empty;
        public PasswordInvitation Invitation { get; init; } = new();
        public int ControlLevel { get; set; }
        public bool Terminated { get; private set; }
        public void TerminateConnection() => Terminated = true;
    }
}


public sealed class WdsTheoryAttribute : TheoryAttribute
{
    public WdsTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("EDUSTREAM_WDS_SMOKE") != "1")
            Skip = "실제 Windows WDS 검증은 EDUSTREAM_WDS_SMOKE=1로 별도 실행";
    }
}

[CollectionDefinition("WDS integration", DisableParallelization = true)]
public sealed class WdsIntegrationCollection { }
