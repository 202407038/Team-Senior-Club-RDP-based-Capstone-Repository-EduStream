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
    public async Task ValidRequest_ShouldRaisePasswordHandoff_AndWithdrawItOnRevoke()
    {
        // 9월 1주차 이월/3주차 2번: 생성한 초대 비밀번호가 사용 후 사라지지 않고
        // 교수자 앱이 내부 이벤트/조회로 인계받을 수 있어야 한다. TCP 패킷에는 실리지 않는다.
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");

        RdpInvitationHandoff? handoff = null;
        rig.SessionManager.RdpInvitationPasswordReady += h => handoff = h;
        string? withdrawnFor = null;
        rig.SessionManager.RdpInvitationPasswordWithdrawn += p => withdrawnFor = p;

        var invitation = Assert.IsType<RdpInvitationPacket>(
            await rig.RequestInvitationAsync(alice, "Alice", Guid.NewGuid()));

        Assert.NotNull(handoff);
        Assert.Equal("Alice", handoff!.ParticipantId);
        Assert.Equal(invitation.InvitationId, handoff.InvitationId);
        Assert.False(string.IsNullOrWhiteSpace(handoff.Password));
        Assert.Same(handoff, rig.SessionManager.TryGetPendingInvitationHandoff("Alice"));

        // 비밀번호가 초대 패킷(네트워크로 나가는 값)에는 포함되지 않아야 한다.
        Assert.DoesNotContain(handoff.Password, invitation.ConnectionString);
        Assert.DoesNotContain(handoff.Password, invitation.ToString());

        var leavePacket = PacketFactory.CreateSessionLeave(
            senderId: "Alice", displayName: "Alice", reason: "사용자 요청",
            sessionId: rig.SessionManager.CurrentSession?.SessionId);
        await alice.SendAsync(leavePacket);

        await WaitUntilAsync(() => withdrawnFor == "Alice", DefaultWait);
        Assert.Null(rig.SessionManager.TryGetPendingInvitationHandoff("Alice"));
    }

    [Fact]
    public async Task OneParticipantLeaves_ShouldOnlyRevokeThatParticipantsInvitation_OtherRemainsActive()
    {
        // 9월 3주차 2번: 학생 두 명이 각각 초대를 받은 상태에서 한 명이 이탈해도
        // 다른 학생의 초대/참여는 유지되어야 한다.
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");
        var bob = await rig.ConnectAndJoinAsync("Bob");

        var aliceInvitation = Assert.IsType<RdpInvitationPacket>(
            await rig.RequestInvitationAsync(alice, "Alice", Guid.NewGuid()));
        var bobInvitation = Assert.IsType<RdpInvitationPacket>(
            await rig.RequestInvitationAsync(bob, "Bob", Guid.NewGuid()));
        Assert.Equal(2, rig.SessionManager.RdpInvitationCount);

        var leavePacket = PacketFactory.CreateSessionLeave(
            senderId: "Alice", displayName: "Alice", reason: "사용자 요청",
            sessionId: rig.SessionManager.CurrentSession?.SessionId);
        await alice.SendAsync(leavePacket);

        await WaitUntilAsync(() => rig.Fake.RevokedInvitationIds.Contains(aliceInvitation.InvitationId), DefaultWait);

        Assert.Contains(aliceInvitation.InvitationId, rig.Fake.RevokedInvitationIds);
        Assert.DoesNotContain(bobInvitation.InvitationId, rig.Fake.RevokedInvitationIds);
        Assert.Equal(1, rig.SessionManager.RdpInvitationCount);
        Assert.Equal(1, rig.SessionManager.ParticipantCount);
        Assert.Contains("Bob", rig.SessionManager.ParticipantNames);

        // Bob은 세션/RDP 초대 모두 계속 정상 상태여야 한다 — 채팅으로 생존을 확인한다.
        await bob.SendAsync(PacketFactory.CreateChat(
            senderId: "Bob", sender: "Bob", message: "still-here", sessionId: rig.SessionManager.CurrentSession?.SessionId));
        await WaitUntilAsync(
            () => rig.ServerLog.Snapshot().Any(e => e.Contains("[Chat] 브로드캐스트") && e.Contains("Bob")),
            DefaultWait);
    }

    [Fact]
    public async Task OneParticipantReconnects_ShouldReissueOwnInvitationOnly_OtherParticipantUnaffected()
    {
        // 9월 3주차 2번: 한 학생이 재접속(재요청)해도 다른 학생의 초대는 건드리지 않아야 한다.
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");
        var bob = await rig.ConnectAndJoinAsync("Bob");

        var bobInvitation = Assert.IsType<RdpInvitationPacket>(
            await rig.RequestInvitationAsync(bob, "Bob", Guid.NewGuid()));
        var aliceFirst = Assert.IsType<RdpInvitationPacket>(
            await rig.RequestInvitationAsync(alice, "Alice", Guid.NewGuid()));

        // Alice가 재접속하듯 같은 이름으로 초대를 다시 요청한다.
        var aliceSecond = Assert.IsType<RdpInvitationPacket>(
            await rig.RequestInvitationAsync(alice, "Alice", Guid.NewGuid()));

        Assert.Contains(aliceFirst.InvitationId, rig.Fake.RevokedInvitationIds);
        Assert.NotEqual(aliceFirst.InvitationId, aliceSecond.InvitationId);
        Assert.DoesNotContain(bobInvitation.InvitationId, rig.Fake.RevokedInvitationIds);
        Assert.Equal(2, rig.SessionManager.RdpInvitationCount); // Alice(재발급) + Bob
    }

    [Fact]
    public async Task CloseSession_WithMultipleInvitedParticipants_ShouldRevokeAndNotifyBoth()
    {
        // 9월 3주차 2번: 여러 학생이 초대를 받은 상태에서 세션이 종료되면 전원의 초대가
        // 정리되고 각자에게 폐기 알림이 전달되어야 한다.
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");
        var bob = await rig.ConnectAndJoinAsync("Bob");

        var aliceInvitation = Assert.IsType<RdpInvitationPacket>(
            await rig.RequestInvitationAsync(alice, "Alice", Guid.NewGuid()));
        var bobInvitation = Assert.IsType<RdpInvitationPacket>(
            await rig.RequestInvitationAsync(bob, "Bob", Guid.NewGuid()));

        var serializer = new PacketSerializer();
        var aliceRevoked = new TaskCompletionSource<RdpInvitationRevokedPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bobRevoked = new TaskCompletionSource<RdpInvitationRevokedPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.PacketReceived += (packetType, payload) =>
        {
            if (packetType == PacketType.RdpInvitationRevoked)
                aliceRevoked.TrySetResult(serializer.Deserialize<RdpInvitationRevokedPacket>(payload)!);
            return Task.CompletedTask;
        };
        bob.PacketReceived += (packetType, payload) =>
        {
            if (packetType == PacketType.RdpInvitationRevoked)
                bobRevoked.TrySetResult(serializer.Deserialize<RdpInvitationRevokedPacket>(payload)!);
            return Task.CompletedTask;
        };

        await rig.SessionManager.CloseSessionAsync();

        var aliceRevokedPacket = await aliceRevoked.Task.WaitAsync(DefaultWait);
        var bobRevokedPacket = await bobRevoked.Task.WaitAsync(DefaultWait);

        Assert.Equal(aliceInvitation.InvitationId, aliceRevokedPacket.InvitationId);
        Assert.Equal(bobInvitation.InvitationId, bobRevokedPacket.InvitationId);
        Assert.Contains(aliceInvitation.InvitationId, rig.Fake.RevokedInvitationIds);
        Assert.Contains(bobInvitation.InvitationId, rig.Fake.RevokedInvitationIds);
        Assert.Equal(0, rig.SessionManager.RdpInvitationCount);
    }

    [Fact]
    public async Task InvitationRequestRacingWithSessionClose_ShouldNotLeaveInvitationBehind()
    {
        // 9월 3주차 2번: 초대 생성이 진행되는 도중(await) 세션이 종료되면, 뒤늦게 완료된 초대가
        // 정리 대상에서 빠져 잔류하면 안 된다. 이미 끝난 RevokeAll 이후에 등록되는 경합을 재현한다.
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");

        rig.Fake.DelayNextCreate(TimeSpan.FromMilliseconds(300));
        var requestTask = rig.TryRequestInvitationAsync(alice, "Alice", Guid.NewGuid());

        await WaitUntilAsync(() => rig.Fake.CreateInvitationCalls == 1, DefaultWait);
        await rig.SessionManager.CloseSessionAsync();
        await requestTask;

        await WaitUntilAsync(() => rig.Fake.CreatedInvitationIds.Count == 1, DefaultWait);
        var createdInvitationId = rig.Fake.CreatedInvitationIds[0];

        await WaitUntilAsync(() => rig.Fake.RevokedInvitationIds.Contains(createdInvitationId), DefaultWait);
        Assert.Contains(createdInvitationId, rig.Fake.RevokedInvitationIds);
        Assert.Equal(0, rig.SessionManager.RdpInvitationCount);
    }

    [Fact]
    public async Task InvitationRequestQueuedBehindOwnLeave_ShouldNotLeaveInvitationBehind()
    {
        // 9월 3주차 2번: TcpServerService 수신 루프는 한 연결의 패킷을 순차 처리하므로(대기 중인
        // 핸들러가 끝나야 다음 프레임을 읽는다), 본인 초대 생성이 끝나기 전에 보낸 본인 이탈 요청은
        // 초대 생성 이후에 처리된다. 이 순서에서도 뒤늦게 등록된 초대가 정리되어야 한다.
        await using var rig = await Rig.OpenAsync();
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");
        await rig.ConnectAndJoinAsync("Bob"); // 세션이 완전히 비지 않도록 하여 종료 경로와 분리해서 검증

        rig.Fake.DelayNextCreate(TimeSpan.FromMilliseconds(300));
        var requestTask = rig.TryRequestInvitationAsync(alice, "Alice", Guid.NewGuid());

        await WaitUntilAsync(() => rig.Fake.CreateInvitationCalls == 1, DefaultWait);
        var leavePacket = PacketFactory.CreateSessionLeave(
            senderId: "Alice", displayName: "Alice", reason: "사용자 요청",
            sessionId: rig.SessionManager.CurrentSession?.SessionId);
        await alice.SendAsync(leavePacket);
        await requestTask;

        await WaitUntilAsync(() => rig.Fake.CreatedInvitationIds.Count == 1, DefaultWait);
        var createdInvitationId = rig.Fake.CreatedInvitationIds[0];

        await WaitUntilAsync(() => rig.Fake.RevokedInvitationIds.Contains(createdInvitationId), DefaultWait);
        Assert.Contains(createdInvitationId, rig.Fake.RevokedInvitationIds);
        Assert.Equal(0, rig.SessionManager.RdpInvitationCount);
        Assert.Null(rig.SessionManager.TryGetPendingInvitationHandoff("Alice"));
    }

    [Fact]
    public async Task InvitationRequestRacingWithForcedDisconnect_ShouldNotLeaveInvitationBehind()
    {
        // 9월 3주차 2번: 강제 종료(예: heartbeat 타임아웃)는 본인 연결의 수신 루프가 아니라
        // 별도 백그라운드 루프가 소켓을 끊는다. 초대 생성이 끝나기 전에 이 경로로 이탈이
        // 처리될 수 있어 위 테스트와 달리 실제 동시 실행 경합이다.
        await using var rig = await Rig.OpenAsync(
            heartbeatSendInterval: TimeSpan.FromSeconds(60),
            heartbeatTimeout: TimeSpan.FromMilliseconds(120),
            heartbeatStaleCheckInterval: TimeSpan.FromMilliseconds(20));
        rig.AttachFakeSharing();
        var alice = await rig.ConnectAndJoinAsync("Alice");

        // Alice는 초대 요청 이후 아무 것도 보내지 않으므로 생성 지연(300ms) 중간에
        // heartbeat 타임아웃(120ms)이 먼저 만료되어 강제 disconnect와 경합한다.
        rig.Fake.DelayNextCreate(TimeSpan.FromMilliseconds(300));
        var requestTask = rig.TryRequestInvitationAsync(alice, "Alice", Guid.NewGuid());

        await WaitUntilAsync(() => rig.Fake.CreateInvitationCalls == 1, DefaultWait);
        await requestTask;

        await WaitUntilAsync(() => rig.Fake.CreatedInvitationIds.Count == 1, DefaultWait);
        var createdInvitationId = rig.Fake.CreatedInvitationIds[0];

        await WaitUntilAsync(() => rig.Fake.RevokedInvitationIds.Contains(createdInvitationId), DefaultWait);
        Assert.Contains(createdInvitationId, rig.Fake.RevokedInvitationIds);
        Assert.Equal(0, rig.SessionManager.RdpInvitationCount);
        Assert.Null(rig.SessionManager.TryGetPendingInvitationHandoff("Alice"));
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
        public List<Guid> CreatedInvitationIds { get; } = new();

        private TimeSpan? _nextCreateDelay;

        /// <summary>
        /// 다음 CreateInvitationAsync 호출을 지정한 시간만큼 지연시킨다.
        /// 초대 생성 도중 이탈/세션 종료가 끼어드는 경합 시나리오를 결정적으로 재현하기 위한 테스트 전용 훅.
        /// </summary>
        public void DelayNextCreate(TimeSpan delay) => _nextCreateDelay = delay;

        public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(SharingId);

        public async Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId, string participantId,
            Guid connectionId, string invitationPassword, DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
        {
            CreateInvitationCalls++;
            if (_nextCreateDelay is { } delay)
            {
                _nextCreateDelay = null;
                await Task.Delay(delay, cancellationToken);
            }

            var invitation = PacketFactory.CreateRdpInvitation(
                senderId: "Server",
                sessionId: sessionId,
                participantId: participantId,
                sharingId: sharingId,
                invitationId: Guid.NewGuid(),
                connectionId: connectionId,
                connectionString: "fake-connection-string",
                expiresAt: expiresAt);
            CreatedInvitationIds.Add(invitation.InvitationId);
            return invitation;
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

        public static async Task<Rig> OpenAsync(
            TimeSpan? heartbeatSendInterval = null,
            TimeSpan? heartbeatTimeout = null,
            TimeSpan? heartbeatStaleCheckInterval = null)
        {
            var serializer = new PacketSerializer();
            var port = GetFreePort();
            var logSink = new InMemoryLogSink();
            var tcpServer = new TcpServerService(logSink, serializer);
            var sessionManager = new SessionManager(logSink, tcpServer);
            var heartbeat = new HeartbeatService(sessionManager, tcpServer, logSink,
                sendInterval: heartbeatSendInterval ?? TimeSpan.FromSeconds(60),
                timeout: heartbeatTimeout,
                staleCheckInterval: heartbeatStaleCheckInterval);

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

        /// <summary>
        /// 경합 테스트용: 세션 종료 등으로 응답 자체가 오지 않을 수 있으므로 타임아웃을 예외로 던지지 않는다.
        /// </summary>
        public async Task<BasePacket?> TryRequestInvitationAsync(TcpClientService client, string participantId, Guid connectionId)
        {
            try
            {
                return await RequestInvitationAsync(client, participantId, connectionId);
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
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
