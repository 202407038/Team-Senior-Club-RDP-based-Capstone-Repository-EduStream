using System.Net;
using System.Net.Sockets;
using EduStream.Client.Services;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Core.Utils;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당(U03): 공유 멈춤·재시작 때 화면을 받던 학생과 공유 전 대기 학생이 조작 없이 초대를 다시 요청하도록
/// 서버가 재요청 알림을 보내는지, 퇴장·보기 철회·세션 종료 후에는 보내지 않는지 로컬 TCP와 공유 서비스 대역으로 검증합니다.
/// </summary>
public sealed class SharingRestartRestoreTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);

    [Fact]
    public void RdpSharingStarted_IsKnownAckCode()
    {
        Assert.True(PacketContractUtility.IsKnownAckCode(AckCodes.RdpSharingStarted));
    }

    [Fact]
    public async Task RequestBeforeSharing_ThenSharingStarts_WaiterIsNotified()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");

        var rejected = await rig.RequestInvitationAsync(alice, Guid.NewGuid());
        Assert.Equal(ErrorCodes.RdpSharingNotStarted, Assert.IsType<ErrorPacket>(rejected).ErrorCode);
        Assert.Equal(1, rig.SessionManager.ScreenWaiterCount);

        rig.SessionManager.AttachRdpSharing(rig.Sharing, Guid.NewGuid());

        var notice = await alice.SharingStarted.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(rig.SessionManager.CurrentSession!.SessionId, notice.SessionId);
        Assert.Equal(0, rig.SessionManager.ScreenWaiterCount);
    }

    [Fact]
    public async Task PauseAndRestart_InvitedStudentIsNotified_AndNewConnectionGetsFreshInvitation()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        rig.SessionManager.AttachRdpSharing(rig.Sharing, Guid.NewGuid());
        var firstConnection = Guid.NewGuid();
        var first = Assert.IsType<RdpInvitationPacket>(await rig.RequestInvitationAsync(alice, firstConnection));

        await rig.SessionManager.DetachRdpSharingAsync();
        var revoked = await alice.Revoked.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(first.InvitationId, revoked.InvitationId);
        Assert.Equal(1, rig.SessionManager.ScreenWaiterCount);

        rig.SessionManager.AttachRdpSharing(rig.Sharing, Guid.NewGuid());
        await alice.SharingStarted.Reader.ReadAsync().AsTask().WaitAsync(Wait);

        // 학생 앱은 알림을 받으면 새 연결 ID로 다시 요청한다. 이전 초대와 섞이지 않아야 한다.
        var secondConnection = Guid.NewGuid();
        var second = Assert.IsType<RdpInvitationPacket>(await rig.RequestInvitationAsync(alice, secondConnection));
        Assert.Equal(secondConnection, second.ConnectionId);
        Assert.NotEqual(first.InvitationId, second.InvitationId);
        Assert.Equal(1, rig.SessionManager.RdpInvitationCount);
    }

    [Fact]
    public async Task StudentWhoLeftDuringPause_IsNotNotified()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");
        rig.SessionManager.AttachRdpSharing(rig.Sharing, Guid.NewGuid());
        await rig.RequestInvitationAsync(alice, Guid.NewGuid());
        await rig.RequestInvitationAsync(bob, Guid.NewGuid());
        await rig.SessionManager.DetachRdpSharingAsync();
        Assert.Equal(2, rig.SessionManager.ScreenWaiterCount);

        await alice.Client.SendAsync(PacketFactory.CreateSessionLeave(
            senderId: "Alice", displayName: "Alice", reason: "테스트 퇴장", sessionId: rig.SessionManager.CurrentSession!.SessionId));
        await WaitUntilAsync(() => rig.SessionManager.ScreenWaiterCount == 1);

        rig.SessionManager.AttachRdpSharing(rig.Sharing, Guid.NewGuid());

        await bob.SharingStarted.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        await Task.Delay(100);
        Assert.False(alice.SharingStarted.Reader.TryRead(out _));
    }

    [Fact]
    public async Task StudentWhoRevokedViewing_IsNotNotified()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");
        rig.SessionManager.AttachRdpSharing(rig.Sharing, Guid.NewGuid());
        await rig.RequestInvitationAsync(alice, Guid.NewGuid());
        await rig.RequestInvitationAsync(bob, Guid.NewGuid());
        await rig.SessionManager.DetachRdpSharingAsync();

        Assert.True(await rig.SessionManager.UpdateParticipantPermissionsAsync("Alice", allowViewing: false, allowControl: false));
        Assert.Equal(1, rig.SessionManager.ScreenWaiterCount);

        rig.SessionManager.AttachRdpSharing(rig.Sharing, Guid.NewGuid());

        await bob.SharingStarted.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        await Task.Delay(100);
        Assert.False(alice.SharingStarted.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ViewingRevokedBeforeRequest_IsNotQueued()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        Assert.True(await rig.SessionManager.UpdateParticipantPermissionsAsync("Alice", allowViewing: false, allowControl: false));

        await rig.RequestInvitationAsync(alice, Guid.NewGuid());

        Assert.Equal(0, rig.SessionManager.ScreenWaiterCount);
    }

    [Fact]
    public async Task StudentWhoNeverRequestedScreen_IsNotNotified()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");
        await rig.RequestInvitationAsync(bob, Guid.NewGuid());

        rig.SessionManager.AttachRdpSharing(rig.Sharing, Guid.NewGuid());

        await bob.SharingStarted.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        await Task.Delay(100);
        Assert.False(alice.SharingStarted.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DisconnectedStudent_IsRemovedFromWaiters()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        await rig.RequestInvitationAsync(alice, Guid.NewGuid());
        Assert.Equal(1, rig.SessionManager.ScreenWaiterCount);

        alice.Client.Dispose();

        await WaitUntilAsync(() => rig.SessionManager.ScreenWaiterCount == 0);
    }

    [Fact]
    public async Task CloseSession_ClearsWaiters()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        await rig.RequestInvitationAsync(alice, Guid.NewGuid());
        Assert.Equal(1, rig.SessionManager.ScreenWaiterCount);

        await rig.CloseAsync();

        Assert.Equal(0, rig.SessionManager.ScreenWaiterCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건이 제한 시간 안에 충족되지 않았습니다.");
            await Task.Delay(20);
        }
    }

    private sealed class Student(string displayName, TcpClientService client)
    {
        public string DisplayName { get; } = displayName;
        public TcpClientService Client { get; } = client;
        public System.Threading.Channels.Channel<AckPacket> SharingStarted { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<AckPacket>();
        public System.Threading.Channels.Channel<RdpInvitationRevokedPacket> Revoked { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<RdpInvitationRevokedPacket>();
        public System.Threading.Channels.Channel<BasePacket> Responses { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<BasePacket>();
    }

    /// <summary>3번 실제 공유 서비스 없이 초대 발급/폐기만 흉내 내는 대역입니다.</summary>
    private sealed class FakeSharing : IRdpSharingService
    {
        public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());

        public Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId, string participantId,
            Guid connectionId, string invitationPassword, DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PacketFactory.CreateRdpInvitation(
                senderId: "Server", sessionId: sessionId, participantId: participantId, sharingId: sharingId,
                invitationId: Guid.NewGuid(), connectionId: connectionId,
                connectionString: "fake-connection-string", expiresAt: expiresAt));

        public Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<Student> _students = new();
        private readonly PacketSerializer _serializer = new();
        private bool _closed;

        public SessionManager SessionManager { get; }
        public FakeSharing Sharing { get; } = new();
        public int Port { get; }

        private Rig(SessionManager sessionManager, int port)
        {
            SessionManager = sessionManager;
            Port = port;
        }

        public static async Task<Rig> OpenAsync()
        {
            var log = new InMemoryLogSink();
            var sessionManager = new SessionManager(log, new TcpServerService(log, new PacketSerializer()));
            var port = GetFreePort();
            await sessionManager.OpenSessionAsync("SharingRestoreTest", port);
            return new Rig(sessionManager, port);
        }

        public async Task<Student> JoinAsync(string displayName)
        {
            var client = new TcpClientService(new InMemoryLogSink(), _serializer);
            var student = new Student(displayName, client);
            var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PacketReceived += (packetType, payload) =>
            {
                switch (packetType)
                {
                    case PacketType.Ack:
                        var ack = _serializer.Deserialize<AckPacket>(payload)!;
                        if (ack.AckCode == AckCodes.SessionJoined) joined.TrySetResult();
                        else if (ack.AckCode == AckCodes.RdpSharingStarted) student.SharingStarted.Writer.TryWrite(ack);
                        break;
                    case PacketType.Error:
                        student.Responses.Writer.TryWrite(_serializer.Deserialize<ErrorPacket>(payload)!);
                        break;
                    case PacketType.RdpInvitation:
                        student.Responses.Writer.TryWrite(_serializer.Deserialize<RdpInvitationPacket>(payload)!);
                        break;
                    case PacketType.RdpInvitationRevoked:
                        student.Revoked.Writer.TryWrite(_serializer.Deserialize<RdpInvitationRevokedPacket>(payload)!);
                        break;
                }
                return Task.CompletedTask;
            };
            await client.ConnectAsync("127.0.0.1", Port);
            await client.SendAsync(PacketFactory.CreateSessionJoin(
                senderId: displayName, displayName: displayName, targetAddress: "127.0.0.1", targetPort: Port));
            await joined.Task.WaitAsync(Wait);
            _students.Add(student);
            return student;
        }

        /// <summary>초대 또는 오류 응답 하나를 기다립니다.</summary>
        public async Task<BasePacket> RequestInvitationAsync(Student student, Guid connectionId)
        {
            await student.Client.SendAsync(PacketFactory.CreateRdpInvitationRequest(
                senderId: student.DisplayName, sessionId: SessionManager.CurrentSession!.SessionId,
                participantId: student.DisplayName, connectionId: connectionId));
            return await student.Responses.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        }

        public async Task CloseAsync()
        {
            _closed = true;
            await SessionManager.CloseSessionAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var student in _students)
            {
                try { student.Client.Dispose(); } catch { }
            }
            if (!_closed) await SessionManager.CloseSessionAsync();
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }
    }
}
