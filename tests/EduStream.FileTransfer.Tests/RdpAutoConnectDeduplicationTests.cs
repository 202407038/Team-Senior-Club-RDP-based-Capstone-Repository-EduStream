using System.Collections.Concurrent;
using System.Reflection;
using EduStream.Client.ViewModels;
using EduStream.Core.Collaboration;
using EduStream.Core.Factories;
using EduStream.Core.Models;
using EduStream.Core.Network;

namespace EduStream.FileTransfer.Tests;

/// <summary>수신 순서·중복을 직접 제어하는 ViewModel 회귀. 실제 TLS/TCP 검증은 SecretDeliveryTests에서 수행한다.</summary>
public sealed class RdpAutoConnectDeduplicationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplayedInvitationAndSecret_ConnectOnlyOnce(bool secretFirst)
    {
        await using var rig = new CallbackRig();
        var (invitation, secret) = rig.NewRequest();
        if (secretFirst) rig.DeliverSecret(secret);
        await rig.DeliverInvitationAsync(invitation);
        if (!secretFirst) rig.DeliverSecret(secret);
        rig.DeliverSecret(secret);
        await rig.DeliverInvitationAsync(invitation);
        rig.DeliverSecret(secret);

        var call = Assert.Single(rig.Viewer.Calls);
        Assert.Equal(invitation.InvitationId, call.Invitation.InvitationId);
        Assert.Equal(secret.Password, call.Password);
    }

    [Fact]
    public async Task ConcurrentDuplicateSecrets_ReserveOneConnectionWhileViewerIsPending()
    {
        await using var rig = new CallbackRig();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Viewer.Completion = pending.Task;
        try
        {
            var (invitation, secret) = rig.NewRequest();
            await rig.DeliverInvitationAsync(invitation);
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => rig.DeliverSecret(secret))));
            Assert.Single(rig.Viewer.Calls);
        }
        finally { pending.TrySetResult(); }
    }

    [Theory]
    [InlineData("session")]
    [InlineData("connection")]
    [InlineData("invitation")]
    [InlineData("expired")]
    public async Task InvalidSecret_DoesNotConsumeValidAttempt(string invalidPart)
    {
        await using var rig = new CallbackRig();
        var (invitation, valid) = rig.NewRequest();
        var invalid = invalidPart switch
        {
            "session" => valid with { SessionId = Guid.NewGuid() },
            "connection" => valid with { ConnectionId = Guid.NewGuid() },
            "invitation" => valid with { InvitationId = Guid.NewGuid() },
            _ => valid with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }
        };
        await rig.DeliverInvitationAsync(invitation);
        rig.DeliverSecret(invalid);
        Assert.Empty(rig.Viewer.Calls);
        rig.DeliverSecret(valid);
        Assert.Single(rig.Viewer.Calls);
    }

    [Fact]
    public async Task Reset_RejectsOldPairAndAllowsNewRequestOnce()
    {
        await using var rig = new CallbackRig();
        var first = rig.NewRequest();
        await rig.DeliverInvitationAsync(first.Invitation);
        rig.DeliverSecret(first.Secret);
        Assert.Single(rig.Viewer.Calls);

        await rig.ResetAsync();
        rig.DeliverSecret(first.Secret);
        await rig.DeliverInvitationAsync(first.Invitation);
        Assert.Single(rig.Viewer.Calls);

        var second = rig.NewRequest();
        rig.DeliverSecret(first.Secret);
        await rig.DeliverInvitationAsync(first.Invitation);
        rig.DeliverSecret(second.Secret);
        await rig.DeliverInvitationAsync(second.Invitation);
        rig.DeliverSecret(second.Secret);
        Assert.Equal(2, rig.Viewer.Calls.Count);
        Assert.Equal(second.Invitation.ConnectionId, rig.Viewer.Calls.Last().Invitation.ConnectionId);
    }

    [Fact]
    public async Task FailedViewer_DuplicateDoesNotRetryButFreshRequestCan()
    {
        await using var rig = new CallbackRig();
        rig.Viewer.Completion = Task.FromException(new InvalidOperationException("controlled viewer failure"));
        var first = rig.NewRequest();
        await rig.DeliverInvitationAsync(first.Invitation);
        rig.DeliverSecret(first.Secret);
        rig.DeliverSecret(first.Secret);
        Assert.Single(rig.Viewer.Calls);
        Assert.Contains("RDP 연결 실패", rig.ViewModel.RdpStatusText);

        await rig.ResetAsync();
        rig.Viewer.Completion = Task.CompletedTask;
        var next = rig.NewRequest();
        await rig.DeliverInvitationAsync(next.Invitation);
        rig.DeliverSecret(next.Secret);
        Assert.Equal(2, rig.Viewer.Calls.Count);
    }

    private sealed record ConnectCall(RdpInvitationPacket Invitation, string Password);

    private sealed class RecordingViewer : IRdpViewerService
    {
        public ConcurrentQueue<ConnectCall> Calls { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public event Action<RdpConnectionStatus>? StatusChanged { add { } remove { } }
        public Task ConnectAsync(RdpInvitationPacket invitation, string invitationPassword, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(new(invitation, invitationPassword));
            return Completion;
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CallbackRig : IAsyncDisposable
    {
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly Guid _sessionId = Guid.NewGuid();
        public RecordingViewer Viewer { get; } = new();
        public ClientViewModel ViewModel { get; }

        public CallbackRig() => ViewModel = new ClientViewModel(Viewer);

        // 승인된 참가와 현재 요청만 준비하고 제품 수신 콜백에 입력한다. 엔진/네트워크 성공을 흉내 내는 테스트가 아니다.
        public (RdpInvitationPacket Invitation, RdpInvitationSecretNotice Secret) NewRequest()
        {
            var connection = Guid.NewGuid();
            Set("_isConnected", true);
            Set("_rdpSessionId", _sessionId);
            Set("_rdpParticipant", "Alice");
            Set("_currentRdpConnectionId", connection);
            var invitation = PacketFactory.CreateRdpInvitation("Server", _sessionId, "Alice", Guid.NewGuid(),
                Guid.NewGuid(), connection, "test-only-connection", DateTimeOffset.UtcNow.AddMinutes(5));
            return (invitation, new(_sessionId, invitation.InvitationId, connection,
                "test-only-secret", invitation.ExpiresAt));
        }
        public Task DeliverInvitationAsync(RdpInvitationPacket packet) =>
            (Task)Invoke("HandleRdpInvitationAsync", packet)!;
        public void DeliverSecret(RdpInvitationSecretNotice secret) => Invoke("OnRdpInvitationSecret", secret);
        public Task ResetAsync() => (Task)Invoke("ResetRdpAsync")!;
        private object? Invoke(string method, params object[] args) =>
            typeof(ClientViewModel).GetMethod(method, Flags)!.Invoke(ViewModel, args);
        private void Set(string name, object value) => typeof(ClientViewModel).GetField(name, Flags)!.SetValue(ViewModel, value);
        public async ValueTask DisposeAsync()
        {
            Set("_isConnected", false);
            await ViewModel.ShutdownAsync();
        }
    }
}
