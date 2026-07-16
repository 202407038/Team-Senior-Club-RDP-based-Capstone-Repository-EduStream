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
/// 화면 자동 송신 중 파일 전송과 채팅이 같은 세션에서 독립적으로 유지되는지 검증합니다.
/// </summary>
[Collection(ScreenFileChatIntegrationCollection.Name)]
public sealed class ScreenFileChatIntegrationTests
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ScreenShare_FileTransfer_AndChat_ShouldCompleteWithoutStateCollision()
    {
        var serializer = new PacketSerializer();
        var port = GetFreePort();
        var serverLog = new InMemoryLogSink();
        var tcpServer = new TcpServerService(serverLog, serializer);
        var sessionManager = new SessionManager(serverLog, tcpServer);
        var screenShare = new ScreenShareService(
            sessionManager,
            serverLog,
            new ScreenCaptureSettings
            {
                TargetFrameIntervalMilliseconds = ScreenTransferRules.MinimumFrameIntervalMilliseconds
            });
        var fileDistributor = new FileDistributor(serializer, serverLog);

        var tempRoot = Path.Combine(Path.GetTempPath(), "EduStreamTests", Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(tempRoot, "integration-payload.bin");
        var sourceContent = Enumerable.Range(0, FileTransferRules.MinChunkSize * 3 + 127)
            .Select(index => (byte)(index % 251))
            .ToArray();

        var clients = new List<TcpClientService>();
        var screenCounts = new ConcurrentDictionary<string, int>();
        var fileResults = new ConcurrentDictionary<string, FileReceiveResult>();
        var chatMessages = new ConcurrentDictionary<string, ConcurrentBag<string>>();

        try
        {
            Directory.CreateDirectory(tempRoot);
            await File.WriteAllBytesAsync(sourcePath, sourceContent);
            await sessionManager.OpenSessionAsync("ScreenFileChatIntegration", port);

            await ConnectAndJoinAsync("Alice");
            await ConnectAndJoinAsync("Bob");
            await WaitUntilAsync(() => sessionManager.ParticipantCount == 2, DefaultWait);

            await screenShare.StartContinuousBroadcastAsync();
            await WaitUntilAsync(
                () => screenCounts.GetValueOrDefault("Alice") >= 2
                      && screenCounts.GetValueOrDefault("Bob") >= 2,
                DefaultWait);

            var framesBeforeFile = screenCounts.ToDictionary(entry => entry.Key, entry => entry.Value);
            var packets = await fileDistributor.BuildFilePacketsAsync(
                sourcePath,
                senderId: "Server",
                sessionId: sessionManager.CurrentSession?.SessionId,
                chunkSize: FileTransferRules.MinChunkSize);

            Assert.True(packets.Count > 1);
            foreach (var packet in packets)
            {
                await sessionManager.BroadcastPacketAsync(packet);
                await Task.Delay(10);
            }

            const string completionChat = "파일 전송 완료 후 화면 공유 유지 확인";
            await sessionManager.BroadcastPacketAsync(PacketFactory.CreateSystemChat(
                completionChat,
                sessionManager.CurrentSession?.SessionId));

            await WaitUntilAsync(
                () => fileResults.Count == 2
                      && chatMessages.Values.All(messages => messages.Contains(completionChat)),
                DefaultWait);
            await WaitUntilAsync(
                () => screenCounts.GetValueOrDefault("Alice") >= framesBeforeFile["Alice"] + 2
                      && screenCounts.GetValueOrDefault("Bob") >= framesBeforeFile["Bob"] + 2,
                DefaultWait);

            Assert.True(screenShare.IsStreaming);
            Assert.Equal(2, sessionManager.ParticipantCount);

            foreach (var name in new[] { "Alice", "Bob" })
            {
                var result = fileResults[name];
                Assert.True(result.Success);
                Assert.False(result.Pending);
                Assert.Equal(100, result.ProgressPercent);
                Assert.Contains("저장 완료", result.StatusMessage);
                Assert.Contains(completionChat, chatMessages[name]);
                Assert.True(screenCounts[name] > framesBeforeFile[name]);
                Assert.Equal(sourceContent, await File.ReadAllBytesAsync(result.FilePath!));
            }

            Assert.DoesNotContain(serverLog.Snapshot(), entry =>
                entry.Contains("잘못된 패킷 크기") || entry.Contains("[Packet] 처리 오류"));
        }
        finally
        {
            await screenShare.StopContinuousBroadcastAsync();
            foreach (var client in clients)
            {
                try { client.Dispose(); } catch { }
            }
            await sessionManager.CloseSessionAsync();
            TryDeleteTestDirectory(tempRoot);
        }

        async Task ConnectAndJoinAsync(string displayName)
        {
            var receiver = new FileReceiver();
            var receiveDirectory = Path.Combine(tempRoot, displayName);
            var messages = new ConcurrentBag<string>();
            chatMessages[displayName] = messages;

            var client = new TcpClientService(new InMemoryLogSink(), serializer);
            client.PacketReceived += async (packetType, payload) =>
            {
                switch (packetType)
                {
                    case PacketType.Screen:
                        screenCounts.AddOrUpdate(displayName, 1, (_, count) => count + 1);
                        break;

                    case PacketType.File:
                        var filePacket = serializer.Deserialize<FilePacket>(payload);
                        if (filePacket is not null)
                        {
                            var result = await receiver.TrySaveAsync(filePacket, receiveDirectory);
                            if (result.Success)
                            {
                                fileResults[displayName] = result;
                            }
                        }
                        break;

                    case PacketType.Chat:
                        var chatPacket = serializer.Deserialize<ChatPacket>(payload);
                        if (chatPacket is not null)
                        {
                            messages.Add(chatPacket.Message);
                        }
                        break;
                }
            };

            await client.ConnectAsync("127.0.0.1", port);
            clients.Add(client);
            await client.SendAsync(PacketFactory.CreateSessionJoin(
                senderId: displayName,
                displayName: displayName,
                targetAddress: "127.0.0.1",
                targetPort: port));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

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

    private static void TryDeleteTestDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var tempPath = Path.GetFullPath(Path.GetTempPath());
            if (fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScreenFileChatIntegrationCollection
{
    public const string Name = "ScreenFileChatIntegration";
}
