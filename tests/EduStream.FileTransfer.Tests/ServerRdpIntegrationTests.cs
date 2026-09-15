using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using EduStream.Client.Services;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.ViewModels;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

[Collection("WDS integration")]
public sealed class ServerRdpIntegrationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(25);
    private static Task Invoke(ServerViewModel vm, string method, params object[] args) =>
        (Task)typeof(ServerViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args)!;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task StartFailure_ShouldNotAdvertiseSharing_AndAllowRetry()
    {
        var service = new FakeSharing { FailStart = true };
        var vm = new ServerViewModel(service) { Port = FreePort() };
        try
        {
            await Invoke(vm, "OpenSessionAsync");
            await vm.StartRdpShareAsync();
            Assert.False(vm.IsRdpSharing);
            Assert.False(vm.IsRdpBusy);
            Assert.Contains("실패", vm.RdpStatus);
            service.FailStart = false;
            await vm.StartRdpShareAsync();
            Assert.True(vm.IsRdpSharing);
            Assert.False(vm.StartAutoShareCommand.CanExecute(null));
            await vm.StopRdpShareAsync();
            Assert.False(vm.IsRdpSharing);
            Assert.True(vm.IsSessionOpen);
        }
        finally { await vm.ShutdownAsync(); }
        Assert.True(service.Disposed);
    }

    [Fact]
    public async Task ShutdownDuringStart_ShouldWaitThenStopSharing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeSharing { BeforeStart = async () => { entered.SetResult(); await release.Task; } };
        var vm = new ServerViewModel(service) { Port = FreePort() };
        await Invoke(vm, "OpenSessionAsync");
        var start = vm.StartRdpShareAsync();
        await entered.Task.WaitAsync(Wait);
        var shutdown = vm.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        release.SetResult();
        await Task.WhenAll(start, shutdown).WaitAsync(Wait);
        Assert.False(vm.IsRdpSharing);
        Assert.False(vm.IsSessionOpen);
        Assert.True(service.Disposed);
    }

    [WdsTheory]
    [InlineData(1024)]
    [InlineData(8 * 1024 * 1024)]
    public async Task ProfessorUiFlow_TwoNativeViewers_FileChat_StopAndRestart(int fileSize)
    {
        var vm = new ServerViewModel { Port = FreePort() };
        await using var first = await RdpViewerIntegrationTests.ViewerRig.Create();
        await using var second = await RdpViewerIntegrationTests.ViewerRig.Create();
        var serializer = new PacketSerializer();
        var directory = Path.Combine(Path.GetTempPath(), "EduStreamTests", Guid.NewGuid().ToString("N"));
        var clients = new List<TcpClientService>();
        var fileResults = new ConcurrentDictionary<string, FileReceiveResult>();
        var chatResults = new ConcurrentDictionary<string, bool>();
        var fileFailures = new ConcurrentDictionary<string, FileReceiveResult>();
        var invitations = new ConcurrentDictionary<string, TaskCompletionSource<RdpInvitationPacket>>();
        var pngCount = 0;
        try
        {
            Directory.CreateDirectory(directory);
            await Invoke(vm, "OpenSessionAsync");
            await vm.StartRdpShareAsync();
            Assert.True(vm.IsRdpSharing);
            var session = Guid.Empty;
            async Task<TcpClientService> Join(string name)
            {
                var client = new TcpClientService(new InMemoryLogSink(), serializer);
                clients.Add(client);
                var ack = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
                var receiver = new FileReceiver();
                client.PacketReceived += async (type, payload) =>
                {
                    if (type == PacketType.Ack)
                    {
                        var packet = serializer.Deserialize<AckPacket>(payload);
                        if (packet?.SessionId is Guid id && id != Guid.Empty) ack.TrySetResult(id);
                    }
                    else if (type == PacketType.RdpInvitation)
                        invitations[name].TrySetResult(serializer.Deserialize<RdpInvitationPacket>(payload)!);
                    else if (type == PacketType.File)
                    {
                        var result = await receiver.TrySaveAsync(serializer.Deserialize<FilePacket>(payload)!, Path.Combine(directory, name));
                        if (result.Success) fileResults[name] = result;
                        else if (!result.Pending) fileFailures[name] = result;
                    }
                    else if (type == PacketType.Chat && serializer.Deserialize<ChatPacket>(payload)?.Message == "RDP와 파일 동시 검증")
                        chatResults[name] = true;
                    else if (type == PacketType.Screen) Interlocked.Increment(ref pngCount);
                };
                await client.ConnectAsync("127.0.0.1", vm.Port);
                await client.SendAsync(PacketFactory.CreateSessionJoin(name, name, "127.0.0.1", vm.Port));
                session = await ack.Task.WaitAsync(Wait);
                return client;
            }
            async Task<RdpInvitationPacket> Request(TcpClientService client, string name)
            {
                invitations[name] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await client.SendAsync(PacketFactory.CreateRdpInvitationRequest(name, session, name, Guid.NewGuid()));
                return await invitations[name].Task.WaitAsync(Wait);
            }
            var alice = await Join("Alice");
            var bob = await Join("Bob");
            var invitation1 = await Request(alice, "Alice");
            var invitation2 = await Request(bob, "Bob");
            var password1 = vm.GetInvitationPassword("Alice");
            var password2 = vm.GetInvitationPassword("Bob");
            Assert.False(string.IsNullOrWhiteSpace(password1));
            Assert.NotEqual(password1, password2);
            await first.Viewer.ConnectAsync(invitation1, password1!);
            await first.Connected.Task.WaitAsync(Wait);
            await second.Viewer.ConnectAsync(invitation2, password2!);
            await second.Connected.Task.WaitAsync(Wait);

            var content = Enumerable.Range(0, fileSize).Select(i => (byte)(i % 251)).ToArray();
            var source = Path.Combine(directory, "rdp-lecture.bin");
            await File.WriteAllBytesAsync(source, content);
            vm.ChatInput = "RDP와 파일 동시 검증";
            await Task.WhenAll(Invoke(vm, "SendFileAsync", source, "통합 파일"), Invoke(vm, "SendChatAsync"));
            using var timeout = new CancellationTokenSource(Wait);
            while (fileResults.Count != 2 || chatResults.Count != 2) await Task.Delay(20, timeout.Token);
            foreach (var result in fileResults.Values)
            {
                Assert.Equal(100, result.ProgressPercent);
                Assert.Equal(content, await File.ReadAllBytesAsync(result.FilePath!));
            }
            Assert.Equal(RdpConnectionState.Connected, first.Latest?.State);
            Assert.Equal(RdpConnectionState.Connected, second.Latest?.State);
            Assert.Equal(0, pngCount);
            Assert.DoesNotContain(vm.ActivityLogs, line => line.Contains(password1!) || line.Contains(password2!) || line.Contains(invitation1.ConnectionString));

            // 실제 RDP를 유지한 채 checksum 실패를 주입하고 기존 파일 보존·다음 전송 복구를 확인한다.
            var manager = (SessionManager)typeof(ServerViewModel).GetField("_sessionManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            var distributor = new FileDistributor(serializer, new InMemoryLogSink());
            await foreach (var packet in distributor.StreamFilePacketsAsync(source, "Server", session, FileTransferRules.MinChunkSize))
            {
                packet.Checksum = new string('0', 64);
                await manager.BroadcastPacketAsync(packet);
            }
            while (fileFailures.Count != 2) await Task.Delay(20, timeout.Token);
            foreach (var result in fileResults.Values)
                Assert.Equal(content, await File.ReadAllBytesAsync(result.FilePath!));
            fileResults.Clear();
            chatResults.Clear();
            vm.ChatInput = "RDP와 파일 동시 검증";
            await Task.WhenAll(Invoke(vm, "SendFileAsync", source, "복구 파일"), Invoke(vm, "SendChatAsync"));
            while (fileResults.Count != 2 || chatResults.Count != 2) await Task.Delay(20, timeout.Token);
            Assert.Equal(RdpConnectionState.Connected, first.Latest?.State);
            Assert.Equal(RdpConnectionState.Connected, second.Latest?.State);

            await vm.StopRdpShareAsync();
            await Task.WhenAll(first.Terminated.Task, second.Terminated.Task).WaitAsync(Wait);
            Assert.True(vm.IsSessionOpen);
            Assert.Null(vm.GetInvitationPassword("Alice"));
            Assert.Empty(vm.RdpInvitationParticipants);
            await vm.StartRdpShareAsync();
            var replacement = await Request(alice, "Alice");
            Assert.NotEqual(invitation1.SharingId, replacement.SharingId);
            Assert.NotEqual(password1, vm.GetInvitationPassword("Alice"));
            first.Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await first.Viewer.ConnectAsync(replacement, vm.GetInvitationPassword("Alice")!);
            await first.Connected.Task.WaitAsync(Wait);
        }
        finally
        {
            await vm.ShutdownAsync();
            foreach (var client in clients) client.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class FakeSharing : IRdpSharingService
    {
        public bool FailStart, Disposed;
        public Func<Task>? BeforeStart;
        public async Task<Guid> StartAsync(Guid sessionId, CancellationToken token = default)
        {
            if (BeforeStart is not null) await BeforeStart();
            if (FailStart) throw new InvalidOperationException("mock");
            return Guid.NewGuid();
        }
        public Task<RdpInvitationPacket> CreateInvitationAsync(Guid s, Guid h, string p, Guid c, string password, DateTimeOffset expiry, CancellationToken token = default) => throw new NotImplementedException();
        public Task RevokeInvitationAsync(Guid id, CancellationToken token = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
