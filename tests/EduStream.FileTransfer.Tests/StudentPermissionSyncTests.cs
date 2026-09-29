using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using EduStream.Client.Services;
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
/// 2번 담당(U07): 학생 본인의 보기/제어 허용 상태와 원격 제어 진행 상태를 보호 채널로 동기화하고,
/// 학생의 허용 변경이 서버 레지스트리와 제어 회수에 반영되는지 로컬 TLS/TCP와 입력 엔진 대역으로 검증합니다.
/// </summary>
public sealed class StudentPermissionSyncTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Join_StudentReceivesDefaultOnStatus()
    {
        await using var rig = await Rig.OpenAsync();

        var alice = await rig.JoinAsync("Alice");

        await WaitUntilAsync(() => alice.RoomFrames.Count > 0);
        Assert.Equal(new StudentStatus(true, true, ControlPhase.Idle), alice.Status.Status);
    }

    [Fact]
    public async Task StatusSnapshot_ContainsOnlyTheStudentThemself()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        await rig.JoinAsync("Bob");
        await rig.SessionManager.UpdateParticipantPermissionsAsync("Alice", allowViewing: true, allowControl: false);

        await WaitUntilAsync(() => !alice.Status.Status.AllowControl);

        foreach (var room in alice.RoomFrames)
        {
            var self = Assert.Single(room.Participants);
            Assert.Equal("Alice", self.DisplayName);
            Assert.Equal(room.Connection, self.Connection);
        }
    }

    [Fact]
    public async Task StudentTurnsOffControl_ServerRegistryAndStudentStatusFollow()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        await WaitUntilAsync(() => alice.RoomFrames.Count > 0);

        await alice.Status.SetPermissionsAsync(allowViewing: true, allowControl: false);

        await WaitUntilAsync(() => !alice.Status.Status.AllowControl);
        var server = rig.Snapshot("Alice");
        Assert.True(server.AllowViewing);
        Assert.False(server.AllowControl);
    }

    [Fact]
    public async Task StudentTurnsOffViewing_AlsoTurnsOffControl()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        await WaitUntilAsync(() => alice.RoomFrames.Count > 0);

        await alice.Status.SetPermissionsAsync(allowViewing: false, allowControl: true);

        await WaitUntilAsync(() => !alice.Status.Status.AllowViewing);
        Assert.False(alice.Status.Status.AllowControl);
        Assert.False(rig.Snapshot("Alice").AllowControl);
    }

    [Fact]
    public async Task ProfessorSideChange_IsPushedToStudent()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        await WaitUntilAsync(() => alice.RoomFrames.Count > 0);

        await rig.SessionManager.UpdateParticipantPermissionsAsync("Alice", allowViewing: false, allowControl: false);

        await WaitUntilAsync(() => !alice.Status.Status.AllowViewing);
    }

    [Fact]
    public async Task ControlStatus_GoesOnlyToTargetAndFollowsStop()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");
        rig.SessionManager.AttachRdpSharing(new NoopSharing(), Guid.NewGuid());

        await rig.SessionManager.RequestControlAsync("Alice");
        await WaitUntilAsync(() => alice.Status.Status.UnderControl);

        await rig.SessionManager.StopControlAsync();
        await WaitUntilAsync(() => alice.Status.Status.ControlPhase == ControlPhase.Revoked);
        Assert.Empty(bob.ControlFrames);
    }

    [Fact]
    public async Task StudentStopsControlNow_RevokesActiveControlAndInput()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        rig.SessionManager.AttachRdpSharing(new NoopSharing(), Guid.NewGuid());
        await rig.SessionManager.RequestControlAsync("Alice");
        await WaitUntilAsync(() => alice.Status.Status.UnderControl);

        await alice.Status.SetPermissionsAsync(allowViewing: true, allowControl: false);

        await WaitUntilAsync(() => !alice.Status.Status.UnderControl && !alice.Status.Status.AllowControl);
        Assert.Equal(ControlPhase.Revoked, rig.SessionManager.CurrentControlState!.Phase);
        await WaitUntilAsync(() => !rig.InputGate.Revoked.IsEmpty);
    }

    [Fact]
    public async Task SwitchingTarget_RevokedGoesToPreviousStudent()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");
        rig.SessionManager.AttachRdpSharing(new NoopSharing(), Guid.NewGuid());
        await rig.SessionManager.RequestControlAsync("Alice");
        await WaitUntilAsync(() => alice.Status.Status.UnderControl);

        await rig.SessionManager.RequestControlAsync("Bob");

        await WaitUntilAsync(() => bob.Status.Status.UnderControl);
        await WaitUntilAsync(() => alice.Status.Status.ControlPhase == ControlPhase.Revoked);
    }

    [Fact]
    public void Client_IgnoresOlderRevisionAndOtherConnection()
    {
        var sessionId = Guid.NewGuid();
        var client = new StudentStatusClient(sessionId, new DiscardChannel(), new InMemoryLogSink());
        var me = new ParticipantConnection(sessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
        var other = me with { ConnectionId = Guid.NewGuid() };

        client.HandleFrame(Encode(Room(me, 5, viewing: true, control: false)));
        client.HandleFrame(Encode(Room(me, 4, viewing: true, control: true)));  // 늦게 온 옛 상태
        client.HandleFrame(Encode(Room(other, 9, viewing: false, control: false))); // 다른 연결(재접속 전)

        Assert.Equal(new StudentStatus(true, false, ControlPhase.Idle), client.Status);
    }

    [Fact]
    public void Client_IgnoresOlderControlSequenceAndOtherSession()
    {
        var sessionId = Guid.NewGuid();
        var client = new StudentStatusClient(sessionId, new DiscardChannel(), new InMemoryLogSink());

        client.HandleFrame(Encode(new ControlStatusNotice(sessionId, 3, ControlPhase.Revoked)));
        client.HandleFrame(Encode(new ControlStatusNotice(sessionId, 2, ControlPhase.Active)));
        client.HandleFrame(Encode(new ControlStatusNotice(Guid.NewGuid(), 9, ControlPhase.Active)));

        Assert.Equal(ControlPhase.Revoked, client.Status.ControlPhase);
    }

    [Fact]
    public async Task PermissionChange_ViewingOffForcesControlOffOnTheWire()
    {
        var channel = new DiscardChannel();
        var client = new StudentStatusClient(Guid.NewGuid(), channel, new InMemoryLogSink());

        await client.SetPermissionsAsync(allowViewing: false, allowControl: true);

        var sent = CollaborationMessageCodec.Decode<PermissionChangeRequest>(Assert.Single(channel.Sent), out _);
        Assert.False(sent.AllowViewing);
        Assert.False(sent.AllowControl);
    }

    private static byte[] Encode<T>(T payload) where T : notnull => CollaborationMessageCodec.Encode(Guid.NewGuid(), payload);

    private static RoomJoined Room(ParticipantConnection connection, long revision, bool viewing, bool control) =>
        new(connection, revision, new[] { new ParticipantSnapshot(connection, "나", true, viewing, control, revision) });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건이 제한 시간 안에 충족되지 않았습니다.");
            await Task.Delay(20);
        }
    }

    private sealed class DiscardChannel : ICollaborationChannel
    {
        public List<byte[]> Sent { get; } = new();
        public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
        {
            Sent.Add(frame);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopSharing : IRdpSharingService
    {
        public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());
        public Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId, string participantId,
            Guid connectionId, string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>3번 입력 엔진 대신 허용/회수 호출을 즉시 끝내고 기록합니다.</summary>
    private sealed class RecordingInputGate : IRemoteInputGate
    {
        public ConcurrentQueue<RemoteControlState> Revoked { get; } = new();
        public Task GrantAsync(RemoteControlState requested, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RevokeAsync(RemoteControlState revoked, CancellationToken cancellationToken)
        {
            Revoked.Enqueue(revoked);
            return Task.CompletedTask;
        }
    }

    private sealed class Student(TcpClientService tcp, SecureSessionChannel secure, StudentStatusClient status)
    {
        public TcpClientService Tcp { get; } = tcp;
        public SecureSessionChannel Secure { get; } = secure;
        public StudentStatusClient Status { get; } = status;
        public ConcurrentQueue<RoomJoined> RoomFrames { get; } = new();
        public ConcurrentQueue<ControlStatusNotice> ControlFrames { get; } = new();
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<Student> _students = new();
        private readonly PacketSerializer _serializer = new();

        public X509Certificate2 Certificate { get; } = ProfessorCertificateStore.CreateEphemeral();
        public SessionManager SessionManager { get; }
        public RecordingInputGate InputGate { get; } = new();
        public int Port { get; private set; }

        private Rig()
        {
            var log = new InMemoryLogSink();
            SessionManager = new SessionManager(log, new TcpServerService(log, new PacketSerializer()));
            SessionManager.AttachRemoteInputGate(InputGate);
        }

        public static async Task<Rig> OpenAsync()
        {
            var rig = new Rig();
            for (var attempt = 0; ; attempt++)
            {
                rig.Port = TestPortAllocator.GetFreePortPair();
                try
                {
                    await rig.SessionManager.OpenSessionAsync("StatusSyncTest", rig.Port, default, rig.Certificate);
                    return rig;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 5) { }
            }
        }

        public ParticipantSnapshot Snapshot(string displayName) =>
            SessionManager.Participants.Participants.Single(participant => participant.DisplayName == displayName);

        public async Task<Student> JoinAsync(string displayName)
        {
            var secure = await SecureRoomJoinClient.AuthenticateAsync("127.0.0.1", Port, SessionManager.ConnectionCode!,
                displayName, ReadOnlyMemory<char>.Empty, new InMemoryLogSink(), Wait);
            var status = new StudentStatusClient(secure.SessionId, secure.Connection, new InMemoryLogSink());
            var tcp = new TcpClientService(new InMemoryLogSink(), _serializer);
            var student = new Student(tcp, secure, status);
            secure.FrameReceived += frame =>
            {
                var kind = CollaborationFrameInspector.PeekKind(frame);
                if (kind == CollaborationMessageKind.Participants)
                    student.RoomFrames.Enqueue(CollaborationMessageCodec.Decode<RoomJoined>(frame, out _));
                else if (kind == CollaborationMessageKind.ControlStatus)
                    student.ControlFrames.Enqueue(CollaborationMessageCodec.Decode<ControlStatusNotice>(frame, out _));
                if (StudentStatusClient.Handles(kind)) status.HandleFrame(frame);
                return Task.CompletedTask;
            };

            var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcp.PacketReceived += (type, payload) =>
            {
                if (type == PacketType.Ack && _serializer.Deserialize<AckPacket>(payload)?.AckCode == AckCodes.SessionJoined)
                    joined.TrySetResult();
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
            foreach (var student in _students)
            {
                try { student.Tcp.Dispose(); } catch { }
                await student.Secure.DisposeAsync();
            }
            await SessionManager.CloseSessionAsync();
            Certificate.Dispose();
        }
    }
}
