using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EduStream.Client.Services;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 클라이언트 제거 경로를 검증합니다.
/// - 7월 1주차 2번: heartbeat 타임아웃 제거와 화면 송신 루프의 송신 실패 제거가 같은
///   클라이언트를 동시에 노려도 이중으로 제거하지 않는다(정확히 1회).
/// - 7월 2주차 2번: 다중 클라이언트 송신 중 실패 클라이언트만 제거되고 나머지는 유지되며,
///   서버 로그에 clientId / sessionId / 실패 사유가 남는다.
/// </summary>
public sealed class ClientRemovalFlowTests
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task HeartbeatTimeoutAndScreenSendFailure_OnSameClient_RemoveExactlyOnce()
    {
        var serializer = new PacketSerializer();
        var port = GetFreePort();
        var serverLog = new InMemoryLogSink();
        var tcpServer = new TcpServerService(serverLog, serializer);
        var sessionManager = new SessionManager(serverLog, tcpServer);

        // 타임아웃을 짧게 잡아 heartbeat 컬링이 소켓 종료/화면 송신 실패와 실제로 경쟁하게 한다.
        var heartbeat = new HeartbeatService(
            sessionManager,
            tcpServer,
            serverLog,
            sendInterval: TimeSpan.FromSeconds(60),
            timeout: TimeSpan.FromMilliseconds(200),
            staleCheckInterval: TimeSpan.FromMilliseconds(30));

        var screenShare = new ScreenShareService(
            sessionManager,
            serverLog,
            new ScreenCaptureSettings
            {
                TargetFrameIntervalMilliseconds = ScreenTransferRules.MinimumFrameIntervalMilliseconds
            });

        var client = new TcpClientService(new InMemoryLogSink(), serializer);
        try
        {
            await sessionManager.OpenSessionAsync("RemovalRaceTest", port);

            await client.ConnectAsync("127.0.0.1", port);
            await client.SendAsync(PacketFactory.CreateSessionJoin(
                senderId: "Alice",
                displayName: "Alice",
                targetAddress: "127.0.0.1",
                targetPort: port));
            await WaitUntilAsync(() => sessionManager.ParticipantCount == 1, DefaultWait);

            // 화면 송신 루프와 heartbeat 컬링을 동시에 가동한 뒤 클라이언트 소켓을 끊는다.
            // 이제 같은 clientId에 대해 (1) 수신 루프 read==0, (2) 화면 브로드캐스트 송신 실패,
            // (3) heartbeat 타임아웃 세 경로가 모두 RemoveClientAsync를 호출하려 한다.
            heartbeat.Start();
            await screenShare.StartContinuousBroadcastAsync();
            client.Dispose();

            await WaitUntilAsync(() => sessionManager.ParticipantCount == 0, DefaultWait);

            // 세 경로가 경쟁해도 제거는 정확히 1회여야 한다(ConcurrentDictionary.TryRemove 가드).
            await screenShare.StopContinuousBroadcastAsync();
            heartbeat.Stop();

            Assert.Equal(0, sessionManager.ParticipantCount);

            var removalLogs = serverLog.Snapshot()
                .Count(e => e.Contains("[Session] 연결 끊김 이탈") && e.Contains("Alice"));
            Assert.Equal(1, removalLogs);

            var systemBroadcasts = serverLog.Snapshot()
                .Count(e => e.Contains("[Chat] 시스템 브로드캐스트: Alice님의 연결이 끊어졌습니다."));
            Assert.Equal(1, systemBroadcasts);
        }
        finally
        {
            heartbeat.Stop();
            await screenShare.StopContinuousBroadcastAsync();
            try { client.Dispose(); } catch { }
            await sessionManager.CloseSessionAsync();
        }
    }

    [Fact]
    public async Task BroadcastWithOneDeadClient_RemovesOnlyThatClient_AndLogsReasonWithSessionId()
    {
        var serializer = new PacketSerializer();
        var port = GetFreePort();
        var serverLog = new InMemoryLogSink();
        var tcpServer = new TcpServerService(serverLog, serializer);
        var sessionManager = new SessionManager(serverLog, tcpServer);

        // 타임아웃을 길게 잡아, 이 테스트에서 제거의 원인이 heartbeat 컬링이 아니라
        // '송신/수신 실패'임을 분명히 한다.
        var heartbeat = new HeartbeatService(
            sessionManager,
            tcpServer,
            serverLog,
            sendInterval: TimeSpan.FromSeconds(60),
            timeout: TimeSpan.FromSeconds(60),
            staleCheckInterval: TimeSpan.FromMilliseconds(50));

        var clients = new List<TcpClientService>();
        var chatCounts = new ConcurrentDictionary<string, int>();

        try
        {
            await sessionManager.OpenSessionAsync("RemovalFlowTest", port);
            heartbeat.Start();

            var alice = await ConnectAndJoinAsync("Alice");
            await ConnectAndJoinAsync("Bob");
            await ConnectAndJoinAsync("Charlie");
            await WaitUntilAsync(() => sessionManager.ParticipantCount == 3, DefaultWait);

            // Alice 소켓을 끊는다. 이후 브로드캐스트가 Alice로 나갈 때 송신에 실패한다.
            alice.Dispose();
            clients.Remove(alice);

            // Bob이 채팅을 반복 전송하면 서버가 전원에게 브로드캐스트하며 죽은 Alice로도 송신을 시도한다.
            var bob = clients.First();
            for (var i = 0; i < 5 && sessionManager.ParticipantCount > 2; i++)
            {
                await bob.SendAsync(PacketFactory.CreateChat(
                    senderId: "Bob",
                    sender: "Bob",
                    message: $"drive-{i}",
                    sessionId: sessionManager.CurrentSession?.SessionId));
                await Task.Delay(50);
            }

            await WaitUntilAsync(() => sessionManager.ParticipantCount == 2, DefaultWait);

            // 실패한 Alice만 빠지고 Bob / Charlie는 유지된다.
            Assert.Equal(2, sessionManager.ParticipantCount);
            Assert.DoesNotContain("Alice", sessionManager.ParticipantNames);
            Assert.Contains("Bob", sessionManager.ParticipantNames);
            Assert.Contains("Charlie", sessionManager.ParticipantNames);

            // 세션 이탈 로그에 clientId / sessionId / 실패 사유가 모두 남아야 한다(7월 2주차 2번).
            var sessionId = sessionManager.CurrentSession?.SessionId;
            Assert.Contains(serverLog.Snapshot(), e =>
                e.Contains("[Session] 연결 끊김 이탈") &&
                e.Contains("Alice") &&
                e.Contains("clientId=") &&
                e.Contains($"sessionId={sessionId}") &&
                e.Contains("사유="));

            // 남은 클라이언트로 브로드캐스트가 계속 도달하는지 확인한다.
            await bob.SendAsync(PacketFactory.CreateChat(
                senderId: "Bob",
                sender: "Bob",
                message: "still-broadcasting",
                sessionId: sessionId));
            await WaitUntilAsync(
                () => chatCounts.GetValueOrDefault("Charlie") >= 1,
                DefaultWait);
            Assert.True(chatCounts.GetValueOrDefault("Charlie") >= 1);
        }
        finally
        {
            heartbeat.Stop();
            foreach (var client in clients)
            {
                try { client.Dispose(); } catch { }
            }
            await sessionManager.CloseSessionAsync();
        }

        async Task<TcpClientService> ConnectAndJoinAsync(string displayName)
        {
            var client = new TcpClientService(new InMemoryLogSink(), serializer);
            client.PacketReceived += (packetType, _) =>
            {
                if (packetType == PacketType.Chat)
                    chatCounts.AddOrUpdate(displayName, 1, (_, v) => v + 1);
                return Task.CompletedTask;
            };

            await client.ConnectAsync("127.0.0.1", port);
            clients.Add(client);

            await client.SendAsync(PacketFactory.CreateSessionJoin(
                senderId: displayName,
                displayName: displayName,
                targetAddress: "127.0.0.1",
                targetPort: port));
            return client;
        }
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
        throw new TimeoutException($"조건이 {timeout.TotalSeconds:F1}s 안에 충족되지 않음");
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
}
