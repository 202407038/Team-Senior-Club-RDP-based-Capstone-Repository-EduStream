using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using EduStream.Client.Services;
using EduStream.Client.ViewModels;
using EduStream.Core.Collaboration;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당(U03 남은 범위): RDP 초대 비밀번호를 요청한 학생의 보호 채널로만 보내고, 학생 앱이 초대와 짝지어
/// 수동 입력 없이 뷰어 연결을 시작하는지 로컬 TLS/TCP로 확인합니다. 화면 공유·뷰어는 3·5번 엔진 대신 기록용 대역입니다.
/// </summary>
public sealed class RdpInvitationSecretDeliveryTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InvitationSecret_GoesOnlyToRequestersSecureChannel()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");
        var connectionId = Guid.NewGuid();

        await alice.Tcp.SendAsync(PacketFactory.CreateRdpInvitationRequest(
            "Alice", rig.SessionManager.CurrentSession!.SessionId, "Alice", connectionId));

        var invitationPayload = await alice.Invitations.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        var secret = await alice.Secrets.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        var invitation = new PacketSerializer().Deserialize<RdpInvitationPacket>(invitationPayload)!;
        var issued = Assert.Single(rig.Sharing.Passwords);

        Assert.Equal(invitation.InvitationId, secret.InvitationId);
        Assert.Equal(connectionId, secret.ConnectionId);
        Assert.Equal(rig.SessionManager.CurrentSession!.SessionId, secret.SessionId);
        Assert.Equal(issued.Value, secret.Password);
        // 평문 TCP 초대에는 비밀번호가 실리지 않는다.
        Assert.DoesNotContain(secret.Password, Encoding.UTF8.GetString(invitationPayload));
        Assert.DoesNotContain(secret.Password, secret.ToString());

        await Task.Delay(300);
        Assert.False(bob.Secrets.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ClientViewModel_JoinAutoConnectsWithoutManualPassword()
    {
        await using var rig = await Rig.OpenAsync();
        var viewer = new RecordingViewer();
        var vm = rig.CreateViewModel("Alice", viewer);
        try
        {
            vm.JoinSessionCommand.Execute(null);

            var connect = await viewer.Connects.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            var issued = Assert.Single(rig.Sharing.Passwords);
            Assert.Equal(issued.Key, connect.Invitation.InvitationId);
            Assert.Equal(issued.Value, connect.Password);
            Assert.Equal("Alice", connect.Invitation.ParticipantId);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task ClientViewModel_SharingRestart_AutoReconnectsWithNewSecret()
    {
        await using var rig = await Rig.OpenAsync();
        var viewer = new RecordingViewer();
        var vm = rig.CreateViewModel("Alice", viewer);
        try
        {
            vm.JoinSessionCommand.Execute(null);
            var first = await viewer.Connects.Reader.ReadAsync().AsTask().WaitAsync(Wait);

            await rig.SessionManager.DetachRdpSharingAsync();
            rig.SessionManager.AttachRdpSharing(rig.Sharing, Guid.NewGuid());

            var second = await viewer.Connects.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            Assert.NotEqual(first.Invitation.InvitationId, second.Invitation.InvitationId);
            Assert.Equal(rig.Sharing.Passwords[second.Invitation.InvitationId], second.Password);
            Assert.NotEqual(first.Password, second.Password);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task ClientViewModel_RejoinAfterDrop_AutoReconnectsScreen()
    {
        await using var rig = await Rig.OpenAsync();
        var viewer = new RecordingViewer();
        var vm = rig.CreateViewModel("Alice", viewer);
        try
        {
            vm.JoinSessionCommand.Execute(null);
            var first = await viewer.Connects.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            await WaitUntilAsync(() => typeof(ClientViewModel)
                .GetField("_reconnectToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(vm) is not null);

            // 사용자 퇴장 없이 강의 TCP 연결만 끊어 비정상 단절을 흉내 낸다.
            var tcp = (TcpClientService)typeof(ClientViewModel)
                .GetField("_tcpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(vm)!;
            await tcp.DisconnectAsync();

            var second = await viewer.Connects.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            Assert.True(vm.IsConnected);
            Assert.NotEqual(first.Invitation.ConnectionId, second.Invitation.ConnectionId);
            Assert.Equal(rig.Sharing.Passwords[second.Invitation.InvitationId], second.Password);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task ClientViewModel_EachJoinConnectsOnlyOnce()
    {
        await using var rig = await Rig.OpenAsync();
        var viewer = new RecordingViewer();
        var vm = rig.CreateViewModel("Alice", viewer);
        try
        {
            vm.JoinSessionCommand.Execute(null);
            await viewer.Connects.Reader.ReadAsync().AsTask().WaitAsync(Wait);

            // 같은 초대·비밀번호 쌍으로 두 번 연결하지 않는다.
            await Task.Delay(300);
            Assert.False(viewer.Connects.Reader.TryRead(out _));
        }
        finally { await vm.ShutdownAsync(); }
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

    private sealed record ConnectCall(RdpInvitationPacket Invitation, string Password);

    /// <summary>5번 뷰어 대신 연결 요청만 기록합니다.</summary>
    private sealed class RecordingViewer : IRdpViewerService
    {
        public Channel<ConnectCall> Connects { get; } = Channel.CreateUnbounded<ConnectCall>();
        public event Action<RdpConnectionStatus>? StatusChanged { add { } remove { } }

        public Task ConnectAsync(RdpInvitationPacket invitation, string invitationPassword, CancellationToken cancellationToken = default)
        {
            Connects.Writer.TryWrite(new ConnectCall(invitation, invitationPassword));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>3번 공유 엔진 대신 초대마다 받은 비밀번호를 기록합니다.</summary>
    private sealed class RecordingSharing : IRdpSharingService
    {
        public ConcurrentDictionary<Guid, string> Passwords { get; } = new();

        public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());

        public Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId, string participantId,
            Guid connectionId, string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
        {
            var invitation = PacketFactory.CreateRdpInvitation(
                senderId: "Server", sessionId: sessionId, participantId: participantId, sharingId: sharingId,
                invitationId: Guid.NewGuid(), connectionId: connectionId,
                connectionString: "fake-connection-string", expiresAt: expiresAt);
            Passwords[invitation.InvitationId] = invitationPassword;
            return Task.FromResult(invitation);
        }

        public Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Student(TcpClientService tcp, SecureSessionChannel secure)
    {
        public TcpClientService Tcp { get; } = tcp;
        public SecureSessionChannel Secure { get; } = secure;
        public Channel<RdpInvitationSecretNotice> Secrets { get; } = Channel.CreateUnbounded<RdpInvitationSecretNotice>();
        public Channel<byte[]> Invitations { get; } = Channel.CreateUnbounded<byte[]>();
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<Student> _students = new();
        private readonly PacketSerializer _serializer = new();

        public X509Certificate2 Certificate { get; } = ProfessorCertificateStore.CreateEphemeral();
        public SessionManager SessionManager { get; }
        public RecordingSharing Sharing { get; } = new();
        public int Port { get; private set; }

        private Rig()
        {
            var log = new InMemoryLogSink();
            SessionManager = new SessionManager(log, new TcpServerService(log, new PacketSerializer()));
        }

        public static async Task<Rig> OpenAsync()
        {
            var rig = new Rig();
            for (var attempt = 0; ; attempt++)
            {
                rig.Port = TestPortAllocator.GetFreePortPair();
                try
                {
                    await rig.SessionManager.OpenSessionAsync("SecretTest", rig.Port, ReadOnlyMemory<char>.Empty, rig.Certificate);
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 5) { }
            }
            rig.SessionManager.AttachRdpSharing(rig.Sharing, Guid.NewGuid());
            return rig;
        }

        public ClientViewModel CreateViewModel(string displayName, IRdpViewerService viewer) => new(viewer)
        {
            HostAddress = "127.0.0.1",
            Port = Port,
            ConnectionCode = SessionManager.ConnectionCode!,
            DisplayName = displayName
        };

        public async Task<Student> JoinAsync(string displayName)
        {
            var secure = await SecureRoomJoinClient.AuthenticateAsync("127.0.0.1", Port, SessionManager.ConnectionCode!,
                displayName, ReadOnlyMemory<char>.Empty, new InMemoryLogSink(), Wait);
            var tcp = new TcpClientService(new InMemoryLogSink(), _serializer);
            var student = new Student(tcp, secure);
            secure.FrameReceived += frame =>
            {
                if (CollaborationFrameInspector.PeekKind(frame) == CollaborationMessageKind.RdpInvitationSecret)
                    student.Secrets.Writer.TryWrite(CollaborationMessageCodec.Decode<RdpInvitationSecretNotice>(frame, out _));
                return Task.CompletedTask;
            };
            var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcp.PacketReceived += (type, payload) =>
            {
                if (type == PacketType.Ack && _serializer.Deserialize<AckPacket>(payload)?.AckCode == AckCodes.SessionJoined)
                    joined.TrySetResult();
                else if (type == PacketType.RdpInvitation)
                    student.Invitations.Writer.TryWrite(payload);
                return Task.CompletedTask;
            };
            await tcp.ConnectAsync("127.0.0.1", Port);
            await tcp.SendAsync(PacketFactory.CreateSessionJoin(displayName, displayName, "127.0.0.1", Port, secure.JoinTicket));
            await joined.Task.WaitAsync(Wait);
            _students.Add(student);
            return student;
        }

        public async ValueTask DisposeAsync()
        {
            await SessionManager.CloseSessionAsync();
            foreach (var student in _students)
            {
                try { student.Tcp.Dispose(); } catch { }
                await student.Secure.DisposeAsync();
            }
            Certificate.Dispose();
        }
    }
}
