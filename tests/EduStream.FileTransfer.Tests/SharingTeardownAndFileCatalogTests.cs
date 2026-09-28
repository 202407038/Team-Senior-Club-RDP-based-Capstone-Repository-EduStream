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

        var inputRevoke = await rig.SessionManager.DetachRdpSharingAsync();

        Assert.Equal(RemoteInputRevokeStatus.Confirmed, inputRevoke);
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
    public async Task DetachRdpSharing_WhileGrantPending_DoesNotReviveRequest()
    {
        await using var rig = await Rig.OpenAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());
        await rig.ConnectAndJoinAsync("Alice");
        var grant = rig.InputGate.HoldNextGrant();

        var request = rig.SessionManager.RequestControlAsync("Alice");
        await rig.InputGate.GrantEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await rig.SessionManager.DetachRdpSharingAsync().WaitAsync(TimeSpan.FromSeconds(2));
        grant.TrySetResult();
        await request.WaitAsync(TimeSpan.FromSeconds(2));

        // 공유 중지가 먼저 끝났으므로 늦게 끝난 허용이 Active로 되살아나면 안 된다.
        Assert.Equal(ControlPhase.Revoked, rig.SessionManager.CurrentControlState!.Phase);
        Assert.NotEmpty(rig.InputGate.Revoked);
    }

    [Fact]
    public async Task DetachRdpSharing_DuringTargetSwitch_DoesNotLeaveNewRequest()
    {
        await using var rig = await Rig.OpenAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());
        await rig.ConnectAndJoinAsync("Alice");
        await rig.ConnectAndJoinAsync("Bob");
        await rig.SessionManager.RequestControlAsync("Alice");
        var alice = rig.GetConnection("Alice");
        var bob = rig.GetConnection("Bob");
        var revoke = rig.InputGate.HoldNextRevoke();

        // Bob으로 전환이 Alice 입력 회수 확인을 기다리는 동안 공유를 중지한다.
        var switchTask = rig.SessionManager.RequestControlAsync("Bob");
        await rig.InputGate.RevokeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var detachTask = rig.SessionManager.DetachRdpSharingAsync();
        revoke.TrySetResult();
        await Task.WhenAll(switchTask, detachTask).WaitAsync(TimeSpan.FromSeconds(2));

        var state = rig.SessionManager.CurrentControlState!;
        Assert.Equal(ControlPhase.Revoked, state.Phase);
        Assert.Equal(alice, state.Student);
        Assert.DoesNotContain(rig.InputGate.Granted, g => g.Student == bob);
    }

    [Fact]
    public async Task RequestControl_WhileDetachWaitsForInputRevoke_IsRejected()
    {
        await using var rig = await Rig.OpenAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());
        await rig.ConnectAndJoinAsync("Alice");
        await rig.ConnectAndJoinAsync("Bob");
        await rig.SessionManager.RequestControlAsync("Alice");
        var bob = rig.GetConnection("Bob");
        var revoke = rig.InputGate.HoldNextRevoke();

        // 공유 중지가 Alice 입력 회수 확인을 기다리는 동안 새 제어 요청이 들어온다.
        var detachTask = rig.SessionManager.DetachRdpSharingAsync();
        await rig.InputGate.RevokeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var lateRequest = rig.SessionManager.RequestControlAsync("Bob");
        revoke.TrySetResult();
        await detachTask.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => lateRequest.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(ControlPhase.Revoked, rig.SessionManager.CurrentControlState!.Phase);
        Assert.DoesNotContain(rig.InputGate.Granted, g => g.Student == bob);
    }

    [Fact]
    public async Task RequestControl_AfterDetachWithoutReattach_IsRejected()
    {
        await using var rig = await Rig.OpenAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());
        await rig.ConnectAndJoinAsync("Alice");
        await rig.SessionManager.DetachRdpSharingAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.SessionManager.RequestControlAsync("Alice"));

        Assert.Null(rig.SessionManager.CurrentControlState);
        Assert.Empty(rig.InputGate.Granted);
    }

    [Fact]
    public async Task RequestControl_WithoutSharing_IsRejected()
    {
        await using var rig = await Rig.OpenAsync();
        await rig.ConnectAndJoinAsync("Alice");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.SessionManager.RequestControlAsync("Alice"));

        Assert.Empty(rig.InputGate.Granted);
    }

    [Fact]
    public async Task RequestControl_AfterReattach_BecomesActive()
    {
        await using var rig = await Rig.OpenAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());
        await rig.ConnectAndJoinAsync("Alice");
        await rig.SessionManager.DetachRdpSharingAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());

        // 이전 공유의 취소가 새 공유에서의 요청에 남아 있으면 안 된다.
        await rig.SessionManager.RequestControlAsync("Alice");

        Assert.Equal(ControlPhase.Active, rig.SessionManager.CurrentControlState!.Phase);
    }

    [Fact]
    public async Task DetachRdpSharing_WhileGrantIgnoresCancellation_ReportsPendingUntilRevoked()
    {
        await using var rig = await Rig.OpenAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());
        await rig.ConnectAndJoinAsync("Alice");
        var grant = rig.InputGate.HoldNextGrantIgnoringCancellation();

        var request = rig.SessionManager.RequestControlAsync("Alice");
        await rig.InputGate.GrantEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var inputRevoke = await rig.SessionManager.DetachRdpSharingAsync().WaitAsync(TimeSpan.FromSeconds(2));

        // 승인은 회수됐지만 native 허용이 끝나지 않았으므로 차단 완료로 보고하면 안 된다.
        Assert.Equal(RemoteInputRevokeStatus.Pending, inputRevoke);
        Assert.Equal(ControlPhase.Revoked, rig.SessionManager.CurrentControlState!.Phase);
        Assert.True(rig.SessionManager.IsControlInputRevokePending);

        var revokesBeforeGrantEnds = rig.InputGate.Revoked.Count;
        grant.TrySetResult();
        await request.WaitAsync(TimeSpan.FromSeconds(2));

        // 허용 작업이 끝나면 조정자가 다시 회수하고 대기 상태가 풀린다.
        Assert.False(rig.SessionManager.IsControlInputRevokePending);
        Assert.True(rig.InputGate.Revoked.Count > revokesBeforeGrantEnds);
        Assert.Equal(ControlPhase.Revoked, rig.SessionManager.CurrentControlState!.Phase);
    }

    [Fact]
    public async Task DetachRdpSharing_WhenInputRevokeFails_ReportsFailedAndStillCleansUp()
    {
        await using var rig = await Rig.OpenAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());
        await rig.ConnectAndJoinAsync("Alice");
        await rig.SessionManager.RequestControlAsync("Alice");
        rig.InputGate.FailNextRevoke();

        var inputRevoke = await rig.SessionManager.DetachRdpSharingAsync();

        Assert.Equal(RemoteInputRevokeStatus.Failed, inputRevoke);
        Assert.Equal(ControlPhase.Revoked, rig.SessionManager.CurrentControlState!.Phase);
        // 실패한 회수는 대기열에 남아 다음 중지 때 다시 시도된다.
        Assert.True(rig.SessionManager.IsControlInputRevokePending);
        await rig.SessionManager.StopControlAsync();
        Assert.False(rig.SessionManager.IsControlInputRevokePending);
    }

    [Fact]
    public async Task DetachRdpSharing_WithNoActiveControl_DoesNotThrow()
    {
        await using var rig = await Rig.OpenAsync();
        rig.SessionManager.AttachRdpSharing(new NoopRdpSharingService(), Guid.NewGuid());

        var inputRevoke = await rig.SessionManager.DetachRdpSharingAsync();

        Assert.Equal(RemoteInputRevokeStatus.Confirmed, inputRevoke);
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

    /// <summary>3번 실제 입력 엔진 대신 호출을 기록하고, 필요하면 다음 허용/회수 완료를 붙잡아 두는 대역입니다.</summary>
    private sealed class RecordingInputGate : IRemoteInputGate
    {
        private TaskCompletionSource? _nextGrant;
        private TaskCompletionSource? _nextStubbornGrant;
        private TaskCompletionSource? _nextRevoke;
        private int _failNextRevoke;

        public System.Collections.Concurrent.ConcurrentQueue<RemoteControlState> Granted { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<RemoteControlState> Revoked { get; } = new();
        public TaskCompletionSource GrantEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RevokeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HoldNextGrant() =>
            _nextGrant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HoldNextRevoke() =>
            _nextRevoke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>native 허용이 취소를 무시하고 끝까지 진행되는 경우를 흉내 냅니다.</summary>
        public TaskCompletionSource HoldNextGrantIgnoringCancellation() =>
            _nextStubbornGrant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void FailNextRevoke() => Interlocked.Exchange(ref _failNextRevoke, 1);

        public Task GrantAsync(RemoteControlState requested, CancellationToken cancellationToken)
        {
            Granted.Enqueue(requested);
            GrantEntered.TrySetResult();
            var stubborn = Interlocked.Exchange(ref _nextStubbornGrant, null);
            if (stubborn is not null) return stubborn.Task;
            var hold = Interlocked.Exchange(ref _nextGrant, null);
            return hold?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
        }

        public Task RevokeAsync(RemoteControlState revoked, CancellationToken cancellationToken)
        {
            Revoked.Enqueue(revoked);
            RevokeEntered.TrySetResult();
            if (Interlocked.Exchange(ref _failNextRevoke, 0) == 1)
                return Task.FromException(new InvalidOperationException("입력 차단 실패(대역)"));
            var hold = Interlocked.Exchange(ref _nextRevoke, null);
            return hold?.Task ?? Task.CompletedTask;
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
