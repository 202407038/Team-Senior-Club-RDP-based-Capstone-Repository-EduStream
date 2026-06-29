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
/// heartbeat 송신 루프와 화면 자동 송신 루프가 동시에 TcpServerService.BroadcastAsync를
/// 호출해도 길이-헤더 프레이밍이 깨지지 않고, 어느 쪽도 상대 루프 때문에 클라이언트를
/// 잘못 끊지 않는지 검증합니다. (7월 1주차 2번: heartbeat × 화면 송신 루프 충돌 확인)
/// </summary>
public sealed class ScreenShareHeartbeatConcurrencyTests
{
    [Fact]
    public async Task HeartbeatAndScreenLoops_RunningConcurrently_DeliverBothFrameTypesIntact()
    {
        var serializer = new PacketSerializer();
        var port = GetFreePort();
        var serverLog = new InMemoryLogSink();
        var tcpServer = new TcpServerService(serverLog, serializer);
        var sessionManager = new SessionManager(serverLog, tcpServer);

        // heartbeat는 자주 보내되(100ms) 타임아웃은 길게(60s) 잡아, 동시 부하 검증 중에
        // 비활성 컬링이 끼어들어 클라이언트를 끊는 일이 없도록 한다.
        var heartbeat = new HeartbeatService(
            sessionManager,
            tcpServer,
            serverLog,
            sendInterval: TimeSpan.FromMilliseconds(100),
            timeout: TimeSpan.FromSeconds(60),
            staleCheckInterval: TimeSpan.FromMilliseconds(50));

        var screenShare = new ScreenShareService(
            sessionManager,
            serverLog,
            new ScreenCaptureSettings
            {
                TargetFrameIntervalMilliseconds = ScreenTransferRules.MinimumFrameIntervalMilliseconds
            });

        var clients = new List<TcpClientService>();
        var screenCounts = new ConcurrentDictionary<string, int>();
        var heartbeatCounts = new ConcurrentDictionary<string, int>();

        try
        {
            await sessionManager.OpenSessionAsync("ConcurrencyTest", port);

            await ConnectAndJoinAsync("Alice");
            await ConnectAndJoinAsync("Bob");
            await WaitUntilAsync(() => sessionManager.ParticipantCount == 2, TimeSpan.FromSeconds(2));

            // 두 송신 루프를 동시에 가동한다.
            heartbeat.Start();
            await screenShare.StartContinuousBroadcastAsync();

            // 동시 송신이 충분히 누적되도록 잠시 돌린다.
            await WaitUntilAsync(
                () => screenCounts.Values.DefaultIfEmpty(0).Min() >= 3
                      && heartbeatCounts.Count == 2,
                TimeSpan.FromSeconds(5));

            await screenShare.StopContinuousBroadcastAsync();
            heartbeat.Stop();

            // 1) 두 루프가 동시에 돌았음에도 누구도 끊기지 않았다.
            Assert.Equal(2, sessionManager.ParticipantCount);

            // 2) 각 클라이언트가 화면 프레임과 하트비트를 모두, 깨짐 없이 수신했다.
            //    (프레이밍이 인터리브로 손상됐다면 역직렬화 단계에서 타입을 못 읽어 카운트가 0이 된다.)
            foreach (var name in new[] { "Alice", "Bob" })
            {
                Assert.True(screenCounts.GetValueOrDefault(name) >= 3,
                    $"{name}가 화면 프레임을 충분히 받지 못함: {screenCounts.GetValueOrDefault(name)}");
                Assert.True(heartbeatCounts.GetValueOrDefault(name) >= 1,
                    $"{name}가 하트비트를 받지 못함: {heartbeatCounts.GetValueOrDefault(name)}");
            }

            // 3) 잘못된 프레임 크기(프레이밍 손상) 로그가 서버에 없어야 한다.
            Assert.DoesNotContain(serverLog.Snapshot(), entry => entry.Contains("잘못된 패킷 크기"));
        }
        finally
        {
            heartbeat.Stop();
            await screenShare.StopContinuousBroadcastAsync();
            foreach (var client in clients)
            {
                try { client.Dispose(); } catch { }
            }
            await sessionManager.CloseSessionAsync();
        }

        async Task ConnectAndJoinAsync(string displayName)
        {
            var client = new TcpClientService(new InMemoryLogSink(), serializer);
            client.PacketReceived += (packetType, _) =>
            {
                if (packetType == PacketType.Screen)
                    screenCounts.AddOrUpdate(displayName, 1, (_, v) => v + 1);
                else if (packetType == PacketType.Heartbeat)
                    heartbeatCounts.AddOrUpdate(displayName, 1, (_, v) => v + 1);
                return Task.CompletedTask;
            };

            await client.ConnectAsync("127.0.0.1", port);
            clients.Add(client);

            var joinPacket = PacketFactory.CreateSessionJoin(
                senderId: displayName,
                displayName: displayName,
                targetAddress: "127.0.0.1",
                targetPort: port);
            await client.SendAsync(joinPacket);
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
