using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당: 화면 공유 중지 시 진행 중인 원격 제어 회수, 그리고 SessionFileCatalog가
/// SessionManager에 실제로 연결되어 등록/해제/스냅샷이 동작하는지 검증합니다.
/// </summary>
public sealed class SharingTeardownAndFileCatalogTests
{
    [Fact]
    public async Task DetachRdpSharing_WhileControlActive_RevokesControlAndInput()
    {
        await using var rig = await Rig.OpenAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());
        await rig.ConnectAndJoinAsync("Alice");
        var alice = rig.GetConnection("Alice");

        await rig.SessionManager.RequestControlAsync("Alice");
        Assert.Equal(ControlPhase.Active, rig.SessionManager.CurrentControlState!.Phase);

        await rig.SessionManager.DetachRdpSharingAsync();

        Assert.Equal(ControlPhase.Revoked, rig.SessionManager.CurrentControlState!.Phase);
        Assert.Equal(alice, Assert.Single(rig.InputGate.Revoked).Student);
    }

    [Fact]
    public async Task ReattachRdpSharing_DoesNotAutoReapproveControl()
    {
        await using var rig = await Rig.OpenAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());
        await rig.ConnectAndJoinAsync("Alice");
        await rig.SessionManager.RequestControlAsync("Alice");

        await rig.SessionManager.DetachRdpSharingAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());

        // 화면 수신 자동 복귀와 달리 원격 제어는 교수자가 다시 요청해야 한다.
        Assert.Equal(ControlPhase.Revoked, rig.SessionManager.CurrentControlState!.Phase);
        Assert.Single(rig.InputGate.Granted);
    }

    [Fact]
    public async Task DetachRdpSharing_WithNoActiveControl_DoesNotThrow()
    {
        await using var rig = await Rig.OpenAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());

        await rig.SessionManager.DetachRdpSharingAsync();

        Assert.Null(rig.SessionManager.CurrentControlState);
    }

    [Fact]
    public async Task RegisterAndUnregisterFile_UpdatesCatalogSnapshotAndRevision()
    {
        await using var rig = await Rig.OpenAsync();
        var path = await rig.WriteSourceFileAsync(1024);

        var before = rig.SessionManager.GetFileCatalogSnapshot()!;
        Assert.Empty(before.Files);

        var descriptor = await rig.SessionManager.RegisterFileAsync(path);

        var afterRegister = rig.SessionManager.GetFileCatalogSnapshot()!;
        Assert.True(afterRegister.Revision > before.Revision);
        Assert.Single(afterRegister.Files, f => f.FileId == descriptor.FileId);
        Assert.Equal(64, descriptor.Sha256.Length);
        Assert.Equal(1024, descriptor.Length);

        var expectedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
        Assert.Equal(expectedHash, descriptor.Sha256);

        var removed = rig.SessionManager.UnregisterFile(descriptor.FileId);
        Assert.True(removed);

        var afterUnregister = rig.SessionManager.GetFileCatalogSnapshot()!;
        Assert.True(afterUnregister.Revision > afterRegister.Revision);
        Assert.Empty(afterUnregister.Files);
    }

    [Fact]
    public async Task UnregisterFile_UnknownId_ReturnsFalse()
    {
        await using var rig = await Rig.OpenAsync();

        Assert.False(rig.SessionManager.UnregisterFile(Guid.NewGuid()));
    }

    [Fact]
    public async Task RegisterFile_WithoutOpenSession_Throws()
    {
        var sessionManager = new SessionManager(new InMemoryLogSink(), new TcpServerService(new InMemoryLogSink(), new PacketSerializer()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sessionManager.RegisterFileAsync("dummy.bin"));
        Assert.Throws<InvalidOperationException>(() => sessionManager.UnregisterFile(Guid.NewGuid()));
        Assert.Null(sessionManager.GetFileCatalogSnapshot());
    }

    [Fact]
    public async Task CloseSession_ClearsFileCatalog()
    {
        await using var rig = await Rig.OpenAsync();
        var path = await rig.WriteSourceFileAsync(64);
        await rig.SessionManager.RegisterFileAsync(path);

        await rig.SessionManager.CloseSessionAsync();
        rig.MarkClosed();

        Assert.Null(rig.SessionManager.GetFileCatalogSnapshot());
    }

    private sealed class NoopRdpSharingService : IRdpSharingService
    {
        public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());
        public Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId, string participantId,
            Guid connectionId, string invitationPassword, DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingInputGate : IRemoteInputGate
    {
        public System.Collections.Concurrent.ConcurrentQueue<RemoteControlState> Granted { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<RemoteControlState> Revoked { get; } = new();

        public Task GrantAsync(RemoteControlState requested, CancellationToken cancellationToken)
        {
            Granted.Enqueue(requested);
            return Task.CompletedTask;
        }

        public Task RevokeAsync(RemoteControlState revoked, CancellationToken cancellationToken)
        {
            Revoked.Enqueue(revoked);
            return Task.CompletedTask;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<TcpClientService> _clients = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "EduStream-sharing-file-" + Guid.NewGuid().ToString("N"));
        public SessionManager SessionManager { get; }
        public int Port { get; }
        public RecordingInputGate InputGate { get; } = new();
        private bool _closed;

        private Rig(SessionManager sessionManager, int port)
        {
            SessionManager = sessionManager;
            Port = port;
            Directory.CreateDirectory(Root);
        }

        public static async Task<Rig> OpenAsync()
        {
            var serializer = new PacketSerializer();
            var port = GetFreePort();
            var logSink = new InMemoryLogSink();
            var tcpServer = new TcpServerService(logSink, serializer);
            var sessionManager = new SessionManager(logSink, tcpServer);
            var rig = new Rig(sessionManager, port);
            sessionManager.AttachRemoteInputGate(rig.InputGate);
            await sessionManager.OpenSessionAsync("SharingFileTest", port);
            return rig;
        }

        public async Task<TcpClientService> ConnectAndJoinAsync(string displayName)
        {
            var client = new TcpClientService(new InMemoryLogSink(), new PacketSerializer());
            var ackReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PacketReceived += (packetType, _) =>
            {
                if (packetType is PacketType.Ack or PacketType.Error)
                    ackReceived.TrySetResult(true);
                return Task.CompletedTask;
            };

            await client.ConnectAsync("127.0.0.1", Port);
            var joinPacket = PacketFactory.CreateSessionJoin(
                senderId: displayName, displayName: displayName,
                targetAddress: "127.0.0.1", targetPort: Port);
            await client.SendAsync(joinPacket);
            await ackReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

            _clients.Add(client);
            return client;
        }

        public ParticipantConnection GetConnection(string displayName) =>
            SessionManager.Participants.Participants.Single(p => p.DisplayName == displayName).Connection;

        public async Task<string> WriteSourceFileAsync(int length)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".bin");
            var content = new byte[length];
            new Random(1).NextBytes(content);
            await File.WriteAllBytesAsync(path, content);
            return path;
        }

        public void MarkClosed() => _closed = true;

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients)
            {
                try { client.Dispose(); } catch { }
            }
            _clients.Clear();

            if (!_closed)
                await SessionManager.CloseSessionAsync();

            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
