using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using EduStream.Client.Services;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
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

    [Fact]
    public async Task InvalidPacketType_ShouldBeRejectedByCommonContractWithoutClosingSession()
    {
        await using var rig = await TestRig.OpenAsync();

        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync("127.0.0.1", rig.Port);

        var payload = Encoding.UTF8.GetBytes("{\"MessageType\":999,\"SenderId\":\"invalid-client\",\"DataLength\":0}");
        var frame = new byte[4 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
        payload.CopyTo(frame, 4);

        await rawClient.GetStream().WriteAsync(frame);
        await rawClient.GetStream().FlushAsync();

        await WaitUntilAsync(
            () => rig.ServerLog.Snapshot().Any(entry =>
                entry.Contains("[Packet] 처리 오류") &&
                entry.Contains(ErrorCodes.InvalidPacketType)),
            DefaultWait);

        Assert.True(rig.SessionManager.IsSessionOpen);
        Assert.Equal(0, rig.SessionManager.ParticipantCount);
    }

    [Fact]
    public async Task PoisonPacketFromOneParticipant_ShouldNotDropSessionOrOtherParticipants()
    {
        // 7월 3주차 2번: 교수자 1명·수강생 2명 통합 실행 중 한 연결에서 서버 예외가 나도
        // 세션 전체가 끊기지 않고 나머지 참가자가 유지되며 계속 동작해야 한다.
        await using var rig = await TestRig.OpenAsync();

        var alice = await rig.ConnectAndJoinAsync("Alice");
        var bob = await rig.ConnectAndJoinAsync("Bob");
        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 2, DefaultWait);

        var sessionId = rig.SessionManager.CurrentSession?.SessionId;

        // 통합 흐름 기준선: 예외 이전에도 채팅이 정상적으로 브로드캐스트되는지 먼저 확인한다.
        await alice.SendAsync(PacketFactory.CreateChat(
            senderId: "Alice", sender: "Alice", message: "before-fault", sessionId: sessionId));
        await WaitUntilAsync(
            () => rig.ServerLog.Snapshot().Any(entry => entry.Contains("[Chat] 브로드캐스트") && entry.Contains("Alice")),
            DefaultWait);

        // Alice가 프레이밍/직렬화는 정상이지만 메타데이터가 잘못된 화면 패킷을 보낸다.
        // 서버 핸들러의 ScreenTransferUtility.ValidatePacketMetadata가 예외를 던지고,
        // OnPacketReceivedAsync의 try/catch가 이를 흡수해야 한다. (frameIndex<=0 → InvalidFrameDimensions)
        await alice.SendAsync(PacketFactory.CreateScreenFrame(
            senderId: "Alice",
            frameIndex: 0,
            frameDescription: "poison-frame",
            width: 1920,
            height: 1080,
            encoding: ScreenEncodings.Png,
            content: new byte[] { 1, 2, 3 },
            sessionId: sessionId));

        await WaitUntilAsync(
            () => rig.ServerLog.Snapshot().Any(entry =>
                entry.Contains("[Packet] 처리 오류") &&
                entry.Contains(ErrorCodes.InvalidFrameDimensions)),
            DefaultWait);

        // 예외 이후에도 세션은 열려 있고 두 참가자가 모두 유지되어야 한다.
        Assert.True(rig.SessionManager.IsSessionOpen);
        Assert.Equal(2, rig.SessionManager.ParticipantCount);
        Assert.Contains("Alice", rig.SessionManager.ParticipantNames);
        Assert.Contains("Bob", rig.SessionManager.ParticipantNames);

        // 나머지 참가자(Bob)의 채팅이 여전히 브로드캐스트되어야 세션이 실제로 살아있다고 볼 수 있다.
        await bob.SendAsync(PacketFactory.CreateChat(
            senderId: "Bob", sender: "Bob", message: "after-fault", sessionId: sessionId));
        await WaitUntilAsync(
            () => rig.ServerLog.Snapshot().Any(entry => entry.Contains("[Chat] 브로드캐스트") && entry.Contains("Bob")),
            DefaultWait);
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
