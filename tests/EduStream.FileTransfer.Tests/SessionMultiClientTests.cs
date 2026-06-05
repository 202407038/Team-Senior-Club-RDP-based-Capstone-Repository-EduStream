using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using EduStream.Client.Services;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 실제 TCP 루프백 위에서 SessionManager / TcpServerService / HeartbeatService /
/// TcpClientService를 함께 띄워 다중 클라이언트 안정성과 heartbeat 타임아웃 정책을 검증합니다.
/// 각 테스트는 사용 가능한 포트를 새로 잡아 직렬화된 실행에서도 충돌하지 않도록 합니다.
/// </summary>
public sealed class SessionMultiClientTests
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task ThreeClientsJoin_ShouldReportParticipantCountThree()
    {
        await using var rig = await TestRig.OpenAsync();

        await rig.ConnectAndJoinAsync("Alice");
        await rig.ConnectAndJoinAsync("Bob");
        await rig.ConnectAndJoinAsync("Charlie");

        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 3, DefaultWait);

        Assert.Equal(3, rig.SessionManager.ParticipantCount);
        Assert.Contains("Alice", rig.SessionManager.ParticipantNames);
        Assert.Contains("Bob", rig.SessionManager.ParticipantNames);
        Assert.Contains("Charlie", rig.SessionManager.ParticipantNames);
    }

    [Fact]
    public async Task OneClientAbnormalDisconnect_ShouldDecrementCountAndPreserveOthers()
    {
        await using var rig = await TestRig.OpenAsync();

        var alice = await rig.ConnectAndJoinAsync("Alice");
        var bob = await rig.ConnectAndJoinAsync("Bob");

        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 2, DefaultWait);

        // Alice가 비정상적으로 소켓을 끊는다 — 서버는 read==0으로 감지해 OnClientDisconnectedAsync를 돌려야 한다.
        alice.Dispose();
        rig.ForgetClient(alice);

        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 1, DefaultWait);

        Assert.Equal(1, rig.SessionManager.ParticipantCount);
        Assert.DoesNotContain("Alice", rig.SessionManager.ParticipantNames);
        Assert.Contains("Bob", rig.SessionManager.ParticipantNames);

        // 남은 Bob은 정상 동작해야 한다 — 채팅이 [Chat] 브로드캐스트 로그로 흘러야 한다.
        var chat = PacketFactory.CreateChat(
            senderId: "Bob",
            sender: "Bob",
            message: "still-here",
            sessionId: rig.SessionManager.CurrentSession?.SessionId);
        await bob.SendAsync(chat);

        await WaitUntilAsync(
            () => rig.ServerLog.Snapshot().Any(entry => entry.Contains("[Chat] 브로드캐스트") && entry.Contains("Bob")),
            DefaultWait);
    }

    [Fact]
    public async Task SilentClient_ShouldBeDisconnectedAfterHeartbeatTimeout()
    {
        await using var rig = await TestRig.OpenAsync(
            heartbeatSendInterval: TimeSpan.FromSeconds(60), // 송신 루프가 테스트 중 끼어들지 않게 길게
            heartbeatTimeout: TimeSpan.FromMilliseconds(250),
            heartbeatStaleCheckInterval: TimeSpan.FromMilliseconds(50));

        // 응답을 만들지 않는 raw 클라이언트 — TcpClientService를 띄우되 PacketReceived를 안 붙이면
        // 서버 하트비트가 와도 응답이 없고, 다른 패킷도 보내지 않으므로 LastSeen이 갱신되지 않는다.
        await rig.ConnectAndJoinAsync("Silent");
        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 1, DefaultWait);

        // timeout(250ms) + staleCheckInterval(50ms) 안에 disconnect 되어야 한다.
        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 0, TimeSpan.FromSeconds(3));

        Assert.Equal(0, rig.SessionManager.ParticipantCount);
        Assert.Contains(rig.ServerLog.Snapshot(), entry => entry.Contains("[Heartbeat] 타임아웃 disconnect"));
    }

    [Fact]
    public async Task ResponsiveClient_ShouldNotBeDisconnectedByHeartbeat()
    {
        await using var rig = await TestRig.OpenAsync(
            heartbeatSendInterval: TimeSpan.FromSeconds(60),
            heartbeatTimeout: TimeSpan.FromMilliseconds(500),
            heartbeatStaleCheckInterval: TimeSpan.FromMilliseconds(50));

        var client = await rig.ConnectAndJoinAsync("Active");
        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 1, DefaultWait);

        // timeout 주기보다 짧은 간격으로 패킷을 계속 보내면 LastSeen이 갱신되어 끊기지 않아야 한다.
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
        var sessionId = rig.SessionManager.CurrentSession?.SessionId;
        while (DateTime.UtcNow < deadline)
        {
            var heartbeat = PacketFactory.CreateHeartbeat(senderId: "Active", sessionId: sessionId);
            await client.SendAsync(heartbeat);
            await Task.Delay(100);
        }

        Assert.Equal(1, rig.SessionManager.ParticipantCount);
    }

    [Fact]
    public async Task RapidJoinAndLeave_ShouldKeepParticipantCountConsistent()
    {
        await using var rig = await TestRig.OpenAsync();

        for (var round = 0; round < 5; round++)
        {
            var name = $"Round-{round}";
            var client = await rig.ConnectAndJoinAsync(name);
            await WaitUntilAsync(() => rig.SessionManager.ParticipantNames.Contains(name), DefaultWait);

            client.Dispose();
            rig.ForgetClient(client);
            await WaitUntilAsync(() => !rig.SessionManager.ParticipantNames.Contains(name), DefaultWait);
        }

        Assert.Equal(0, rig.SessionManager.ParticipantCount);
        Assert.Empty(rig.SessionManager.ParticipantNames);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        [CallerMemberName] string caller = "")
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"조건이 {timeout.TotalSeconds:F1}s 안에 충족되지 않음: {caller}");
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

    private sealed class TestRig : IAsyncDisposable
    {
        private readonly List<TcpClientService> _clients = new();

        public InMemoryLogSink ServerLog { get; }
        public TcpServerService TcpServer { get; }
        public SessionManager SessionManager { get; }
        public HeartbeatService Heartbeat { get; }
        public int Port { get; }

        private TestRig(
            InMemoryLogSink serverLog,
            TcpServerService tcpServer,
            SessionManager sessionManager,
            HeartbeatService heartbeat,
            int port)
        {
            ServerLog = serverLog;
            TcpServer = tcpServer;
            SessionManager = sessionManager;
            Heartbeat = heartbeat;
            Port = port;
        }

        public static async Task<TestRig> OpenAsync(
            TimeSpan? heartbeatSendInterval = null,
            TimeSpan? heartbeatTimeout = null,
            TimeSpan? heartbeatStaleCheckInterval = null)
        {
            var serializer = new PacketSerializer();
            var port = GetFreePort();
            var logSink = new InMemoryLogSink();
            var tcpServer = new TcpServerService(logSink, serializer);
            var sessionManager = new SessionManager(logSink, tcpServer);
            var heartbeat = new HeartbeatService(
                sessionManager,
                tcpServer,
                logSink,
                sendInterval: heartbeatSendInterval,
                timeout: heartbeatTimeout,
                staleCheckInterval: heartbeatStaleCheckInterval);

            var rig = new TestRig(logSink, tcpServer, sessionManager, heartbeat, port);

            await sessionManager.OpenSessionAsync("MultiClientTest", port);
            heartbeat.Start();
            return rig;
        }

        public async Task<TcpClientService> ConnectAndJoinAsync(string displayName)
        {
            var serializer = new PacketSerializer();
            var clientLog = new InMemoryLogSink();
            var client = new TcpClientService(clientLog, serializer);

            await client.ConnectAsync("127.0.0.1", Port);

            var joinPacket = PacketFactory.CreateSessionJoin(
                senderId: displayName,
                displayName: displayName,
                targetAddress: "127.0.0.1",
                targetPort: Port);
            await client.SendAsync(joinPacket);

            _clients.Add(client);
            return client;
        }

        public void ForgetClient(TcpClientService client)
        {
            _clients.Remove(client);
        }

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
