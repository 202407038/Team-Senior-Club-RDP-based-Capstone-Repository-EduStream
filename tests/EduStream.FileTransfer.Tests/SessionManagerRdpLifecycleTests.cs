using System.Net;
using System.Net.Sockets;
using EduStream.Client.Services;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 9월 2주차 2번: 강의 세션 참가 자격과 RDP 연결 수명 연결, 이탈/종료 시 연결 정보 정리를 검증합니다.
/// 3번의 실제 IRdpSharingService 구현이 아직 없으므로 대역(FakeRdpSharingService)으로 검증합니다.
/// </summary>
public sealed class SessionManagerRdpLifecycleTests
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task RequestBeforeSharingAttached_ShouldBeRejectedWithSharingNotStarted()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.ConnectAndJoinAsync("Alice");

        var response = await rig.RequestInvitationAsync(alice, "Alice", Guid.NewGuid());

        Assert.IsType<ErrorPacket>(response);
        Assert.Equal(ErrorCodes.RdpSharingNotStarted, ((ErrorPacket)response).ErrorCode);
        Assert.Equal(0, rig.Fake.CreateInvitationCalls);
    }

    [Fact]
    public async Task NonParticipant_RequestingInvitation_ShouldBeRejected()
    {
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();

        var serializer = new PacketSerializer();
        var logSink = new InMemoryLogSink();
        var raw = new TcpClientService(logSink, serializer);
        await raw.ConnectAsync("127.0.0.1", rig.Port);

        var response = await rig.RequestInvitationAsync(raw, "Ghost", Guid.NewGuid());

        Assert.IsType<ErrorPacket>(response);
        Assert.Equal(ErrorCodes.NotParticipant, ((ErrorPacket)response).ErrorCode);
        Assert.Equal(0, rig.Fake.CreateInvitationCalls);

        raw.Dispose();
    }

    [Fact]
    public async Task ImpersonatedParticipantId_ShouldBeRejectedByContractValidation()
    {
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");

        // Alice 연결에서 Bob 행세를 하는 요청 — SenderId/ParticipantId가 실제 인증된 신원과 다르다.
        var response = await rig.RequestInvitationAsync(alice, "Bob", Guid.NewGuid());

        Assert.IsType<ErrorPacket>(response);
        Assert.Equal(ErrorCodes.NotParticipant, ((ErrorPacket)response).ErrorCode);
        Assert.Equal(0, rig.Fake.CreateInvitationCalls);
    }

    [Fact]
    public async Task ValidRequest_ShouldIssueInvitationToRequesterOnly()
    {
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");
        var connectionId = Guid.NewGuid();

        var response = await rig.RequestInvitationAsync(alice, "Alice", connectionId);

        var invitation = Assert.IsType<RdpInvitationPacket>(response);
        Assert.Equal("Alice", invitation.ParticipantId);
        Assert.Equal(connectionId, invitation.ConnectionId);
        Assert.Equal(rig.Fake.SharingId, invitation.SharingId);
        Assert.Equal(1, rig.Fake.CreateInvitationCalls);
        await WaitUntilAsync(
            () => rig.ServerLog.Snapshot().Any(e => e.Contains("[Rdp] 초대 발급") && e.Contains("Alice")),
            DefaultWait);
    }

    [Fact]
    public async Task ReRequest_ShouldRevokePreviousInvitationBeforeIssuingNew()
    {
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");

        var first = await rig.RequestInvitationAsync(alice, "Alice", Guid.NewGuid());
        var firstInvitation = Assert.IsType<RdpInvitationPacket>(first);

        var second = await rig.RequestInvitationAsync(alice, "Alice", Guid.NewGuid());
        var secondInvitation = Assert.IsType<RdpInvitationPacket>(second);

        Assert.NotEqual(firstInvitation.InvitationId, secondInvitation.InvitationId);
        Assert.Equal(2, rig.Fake.CreateInvitationCalls);
        Assert.Contains(firstInvitation.InvitationId, rig.Fake.RevokedInvitationIds);
    }

    [Fact]
    public async Task GracefulLeave_ShouldRevokeActiveInvitation()
    {
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");

        var invitation = Assert.IsType<RdpInvitationPacket>(
            await rig.RequestInvitationAsync(alice, "Alice", Guid.NewGuid()));

        var leavePacket = PacketFactory.CreateSessionLeave(
            senderId: "Alice", displayName: "Alice", reason: "사용자 요청",
            sessionId: rig.SessionManager.CurrentSession?.SessionId);
        await alice.SendAsync(leavePacket);

        await WaitUntilAsync(() => rig.Fake.RevokedInvitationIds.Contains(invitation.InvitationId), DefaultWait);
        Assert.Contains(invitation.InvitationId, rig.Fake.RevokedInvitationIds);
    }

    [Fact]
    public async Task AbruptDisconnect_ShouldRevokeActiveInvitation()
    {
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");

        var invitation = Assert.IsType<RdpInvitationPacket>(
            await rig.RequestInvitationAsync(alice, "Alice", Guid.NewGuid()));

        alice.Dispose();
        rig.ForgetClient(alice);

        await WaitUntilAsync(() => rig.Fake.RevokedInvitationIds.Contains(invitation.InvitationId), DefaultWait);
        Assert.Contains(invitation.InvitationId, rig.Fake.RevokedInvitationIds);
    }

    [Fact]
    public async Task CloseSession_ShouldRevokeAndNotifyRemainingInvitedParticipant()
    {
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");

        var invitation = Assert.IsType<RdpInvitationPacket>(
            await rig.RequestInvitationAsync(alice, "Alice", Guid.NewGuid()));

        var revokedReceived = new TaskCompletionSource<RdpInvitationRevokedPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serializer = new PacketSerializer();
        alice.PacketReceived += (packetType, payload) =>
        {
            if (packetType == PacketType.RdpInvitationRevoked)
            {
                var revoked = serializer.Deserialize<RdpInvitationRevokedPacket>(payload);
                if (revoked is not null)
                    revokedReceived.TrySetResult(revoked);
            }
            return Task.CompletedTask;
        };

        await rig.SessionManager.CloseSessionAsync();

        var revokedPacket = await revokedReceived.Task.WaitAsync(DefaultWait);
        Assert.Equal(invitation.InvitationId, revokedPacket.InvitationId);
        Assert.Equal(RdpFailureReason.SessionClosed, revokedPacket.Reason);
        Assert.Contains(invitation.InvitationId, rig.Fake.RevokedInvitationIds);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException("조건이 제한 시간 안에 충족되지 않았습니다.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>3번의 실제 IRdpSharingService가 없는 상태에서 2번 로직만 독립 검증하기 위한 대역입니다.</summary>
    private sealed class FakeRdpSharingService : IRdpSharingService
    {
        public Guid SharingId { get; } = Guid.NewGuid();
        public int CreateInvitationCalls { get; private set; }
        public List<Guid> RevokedInvitationIds { get; } = new();

        public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(SharingId);

        public Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId, string participantId,
            Guid connectionId, string invitationPassword, DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
        {
            CreateInvitationCalls++;
            var invitation = PacketFactory.CreateRdpInvitation(
                senderId: "Server",
                sessionId: sessionId,
                participantId: participantId,
                sharingId: sharingId,
                invitationId: Guid.NewGuid(),
                connectionId: connectionId,
                connectionString: "fake-connection-string",
                expiresAt: expiresAt);
            return Task.FromResult(invitation);
        }

        public Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default)
        {
            RevokedInvitationIds.Add(invitationId);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<TcpClientService> _clients = new();
        private readonly PacketSerializer _serializer = new();

        public InMemoryLogSink ServerLog { get; }
        public SessionManager SessionManager { get; }
        public HeartbeatService Heartbeat { get; }
        public int Port { get; }
        public FakeRdpSharingService Fake { get; } = new();

        private Rig(InMemoryLogSink serverLog, SessionManager sessionManager, HeartbeatService heartbeat, int port)
        {
            ServerLog = serverLog;
            SessionManager = sessionManager;
            Heartbeat = heartbeat;
            Port = port;
        }

        public static async Task<Rig> OpenAsync()
        {
            var serializer = new PacketSerializer();
            var port = GetFreePort();
            var logSink = new InMemoryLogSink();
            var tcpServer = new TcpServerService(logSink, serializer);
            var sessionManager = new SessionManager(logSink, tcpServer);
            var heartbeat = new HeartbeatService(sessionManager, tcpServer, logSink,
                sendInterval: TimeSpan.FromSeconds(60));

            var rig = new Rig(logSink, sessionManager, heartbeat, port);
            await sessionManager.OpenSessionAsync("RdpLifecycleTest", port);
            heartbeat.Start();
            return rig;
        }

        public void AttachFakeSharing() => SessionManager.AttachRdpSharing(Fake, Fake.SharingId);

        public async Task<TcpClientService> ConnectAndJoinAsync(string displayName)
        {
            var clientLog = new InMemoryLogSink();
            var client = new TcpClientService(clientLog, _serializer);
            await client.ConnectAsync("127.0.0.1", Port);

            var joinPacket = PacketFactory.CreateSessionJoin(
                senderId: displayName, displayName: displayName,
                targetAddress: "127.0.0.1", targetPort: Port);
            await client.SendAsync(joinPacket);

            var ackReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PacketReceived += (packetType, _) =>
            {
                if (packetType is PacketType.Ack or PacketType.Error)
                    ackReceived.TrySetResult(true);
                return Task.CompletedTask;
            };
            await ackReceived.Task.WaitAsync(DefaultWait);

            _clients.Add(client);
            return client;
        }

        public async Task<BasePacket> RequestInvitationAsync(TcpClientService client, string participantId, Guid connectionId)
        {
            var sessionId = SessionManager.CurrentSession?.SessionId ?? Guid.Empty;
            var request = PacketFactory.CreateRdpInvitationRequest(
                senderId: participantId, sessionId: sessionId,
                participantId: participantId, connectionId: connectionId);

            var responseReceived = new TaskCompletionSource<BasePacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PacketReceived += Handler;
            await client.SendAsync(request);
            try
            {
                return await responseReceived.Task.WaitAsync(DefaultWait);
            }
            finally
            {
                client.PacketReceived -= Handler;
            }

            Task Handler(PacketType packetType, byte[] payload)
            {
                switch (packetType)
                {
                    case PacketType.RdpInvitation:
                        responseReceived.TrySetResult(_serializer.Deserialize<RdpInvitationPacket>(payload)!);
                        break;
                    case PacketType.Error:
                        responseReceived.TrySetResult(_serializer.Deserialize<ErrorPacket>(payload)!);
                        break;
                }
                return Task.CompletedTask;
            }
        }

        public void ForgetClient(TcpClientService client) => _clients.Remove(client);

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients)
            {
                try { client.Dispose(); } catch { }
            }
            _clients.Clear();

            Heartbeat.Stop();
            await SessionManager.CloseSessionAsync();
        }
    }
}
