using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당(U03): 비정상 끊김 뒤 재연결 토큰으로 비밀번호 재입력 없이 다시 참가하는 흐름과,
/// 정상 퇴장·세션 종료·만료·재사용 시 재연결을 막는 규칙을 로컬 TLS/TCP로 검증합니다.
/// </summary>
public sealed class AutoReconnectTests
{
    // Review-only regression probe; no native WDS or desktop input is exercised.
    [Fact]
    public async Task Review_PermissionOff_MustConsumeAppliedAckWithoutTimeout()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        rig.SessionManager.AttachRdpSharing(new ReviewSharingStub(), Guid.NewGuid());
        var revokeAckSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.Secure.FrameReceived += async frame =>
        {
            if (CollaborationFrameInspector.PeekKind(frame) != CollaborationMessageKind.RemoteInputCommand) return;
            var command = CollaborationMessageCodec.Decode<RemoteInputCommandNotice>(frame, out _);
            await alice.Secure.Connection.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(),
                new RemoteInputResultNotice(command.CommandId, command.SessionId, command.Action, true, null)));
            if (command.Action == RemoteInputAction.Revoke) revokeAckSent.TrySetResult();
        };
        var student = rig.SessionManager.Participants.Participants.Single(p => p.DisplayName == "Alice").Connection;
        var router = rig.SessionManager.ReverseCollaboration!;
        const string xml = "<E><A KH=\"x\"/></E>";
        var invitation = new ReverseRdpInvitationNotice
        {
            ContractVersion = ReverseRdpInvitationNotice.CurrentVersion,
            Provider = ReverseRdpInvitationNotice.ProviderName,
            Direction = ReverseRdpInvitationNotice.StudentToProfessor,
            SessionId = student.SessionId, SharingId = Guid.NewGuid(), InvitationId = Guid.NewGuid(),
            ConnectionId = student.ConnectionId, ProfessorId = router.ProfessorId,
            StudentId = ReverseRdpIdentity.For(student), ParticipantId = router.ProfessorId,
            ConnectionString = xml, DataLength = System.Text.Encoding.UTF8.GetByteCount(xml),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            ControlMode = ReverseRdpControlMode.HostGrantedInteractive, ViewOnly = false
        };
        var secret = new ReverseRdpInvitationSecretNotice(invitation.SessionId, invitation.SharingId,
            invitation.InvitationId, invitation.ConnectionId, invitation.StudentId, invitation.ProfessorId,
            "review-secret", invitation.ExpiresAt);
        await alice.Secure.Connection.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), invitation));
        await alice.Secure.Connection.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), secret));
        await WaitUntilAsync(() => router.TryGetInvitation(student.ConnectionId) is not null);
        await rig.SessionManager.RequestControlAsync("Alice").WaitAsync(Wait);
        Assert.Equal(ControlPhase.Active, rig.SessionManager.CurrentControlState!.Phase);
        try
        {
            await alice.Secure.Connection.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(),
                new PermissionChangeRequest(Guid.NewGuid(), true, false)));
            await revokeAckSent.Task.WaitAsync(Wait);
            await Task.Delay(1000);
            Assert.False(rig.SessionManager.IsControlInputRevokePending,
                "Student has sent an Applied ACK, but the server is still waiting on its blocked receive loop.");
        }
        finally
        {
            await alice.DropAsync();
        }
    }

    private sealed class ReviewSharingStub : EduStream.Core.Network.IRdpSharingService
    {
        public Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());
        public Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId, string participantId,
            Guid connectionId, string invitationPassword, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AbnormalDisconnect_TokenRejoinsWithoutPassword()
    {
        await using var rig = await Rig.OpenAsync("room-pass");
        var alice = await rig.JoinAsync("Alice", "room-pass");
        var token = await alice.NextTokenAsync();

        await alice.DropAsync();
        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 0);

        var again = await rig.JoinAsync("Alice", password: "", reconnectToken: token);
        Assert.Equal(1, rig.SessionManager.ParticipantCount);
        Assert.NotEqual(token, await again.NextTokenAsync()); // 재참가마다 새 토큰
    }

    [Fact]
    public async Task Reconnect_RestoresPermissionsTurnedOffBeforeDisconnect()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        var token = await alice.NextTokenAsync();
        await rig.SessionManager.UpdateParticipantPermissionsAsync("Alice", allowViewing: true, allowControl: false);

        await alice.DropAsync();
        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 0);
        await rig.JoinAsync("Alice", reconnectToken: token);

        var restored = rig.SessionManager.Participants.Participants.Single(p => p.DisplayName == "Alice");
        Assert.True(restored.AllowViewing);
        Assert.False(restored.AllowControl);
    }

    [Fact]
    public async Task Token_IsSingleUse()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        var token = await alice.NextTokenAsync();
        await alice.DropAsync();
        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 0);
        var again = await rig.JoinAsync("Alice", reconnectToken: token);
        await again.DropAsync();
        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 0);

        var error = await Assert.ThrowsAsync<SecureJoinException>(() => rig.AuthenticateAsync("Alice", reconnectToken: token));

        Assert.Equal(SecureJoinFailure.ReconnectRejected, error.Failure);
    }

    [Fact]
    public async Task GracefulLeave_RevokesToken()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        var token = await alice.NextTokenAsync();

        await alice.Tcp.SendAsync(PacketFactory.CreateSessionLeave("Alice", "Alice", "퇴장", rig.SessionManager.CurrentSession!.SessionId));
        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 0);
        await alice.DropAsync();

        var error = await Assert.ThrowsAsync<SecureJoinException>(() => rig.AuthenticateAsync("Alice", reconnectToken: token));
        Assert.Equal(SecureJoinFailure.ReconnectRejected, error.Failure);
        Assert.Equal(0, rig.SessionManager.ReconnectGrantCount);
    }

    [Fact]
    public async Task ExpiredWindow_RejectsToken()
    {
        await using var rig = await Rig.OpenAsync();
        rig.SessionManager.ReconnectWindow = TimeSpan.FromMilliseconds(200);
        var alice = await rig.JoinAsync("Alice");
        var token = await alice.NextTokenAsync();
        await alice.DropAsync();
        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 0);

        await Task.Delay(400);

        var error = await Assert.ThrowsAsync<SecureJoinException>(() => rig.AuthenticateAsync("Alice", reconnectToken: token));
        Assert.Equal(SecureJoinFailure.ReconnectRejected, error.Failure);
    }

    [Fact]
    public async Task TokenWithOtherName_IsRejectedAndStillUsableByOwner()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        var token = await alice.NextTokenAsync();
        await alice.DropAsync();
        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 0);

        var error = await Assert.ThrowsAsync<SecureJoinException>(() => rig.AuthenticateAsync("Mallory", reconnectToken: token));
        Assert.Equal(SecureJoinFailure.ReconnectRejected, error.Failure);

        await rig.JoinAsync("Alice", reconnectToken: token);
        Assert.Equal(1, rig.SessionManager.ParticipantCount);
    }

    [Fact]
    public async Task ServerNotYetAwareOfDrop_TokenReplacesOldConnection()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        var token = await alice.NextTokenAsync();

        // 옛 연결을 끊지 않은 채(서버는 아직 연결 중으로 봄) 토큰으로 다시 참가한다.
        var again = await rig.JoinAsync("Alice", reconnectToken: token);

        Assert.Equal(1, rig.SessionManager.ParticipantCount);
        await alice.Secure.Connection.Completion.WaitAsync(Wait); // 옛 보호 채널은 정리됨
        Assert.False(again.Secure.Connection.IsClosed);
    }

    [Fact]
    public async Task CloseSession_SendsSessionEndedAndClearsTokens()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.JoinAsync("Alice");
        await alice.NextTokenAsync();

        // 학생 쪽 연결을 닫기 전에 알림 수신을 확인해야 하므로 리그 정리 대신 세션만 먼저 닫는다.
        await rig.SessionManager.CloseSessionAsync();

        var ended = await alice.Ended.Task.WaitAsync(Wait);
        Assert.NotEqual(Guid.Empty, ended.SessionId);
        Assert.Equal(0, rig.SessionManager.ReconnectGrantCount);
    }

    [Fact]
    public async Task Scheduler_RetriesUntilSuccess()
    {
        var attempts = 0;
        var result = await ReconnectScheduler.RunAsync(
            (_, _) => Task.FromResult(++attempts < 3 ? ReconnectAttemptOutcome.RetryLater : ReconnectAttemptOutcome.Succeeded),
            TimeSpan.FromSeconds(5), [TimeSpan.FromMilliseconds(10)]);

        Assert.True(result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Scheduler_StopsOnGiveUp()
    {
        var attempts = 0;
        var result = await ReconnectScheduler.RunAsync(
            (_, _) => { attempts++; return Task.FromResult(ReconnectAttemptOutcome.GiveUp); },
            TimeSpan.FromSeconds(5), [TimeSpan.FromMilliseconds(10)]);

        Assert.False(result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Scheduler_StopsWhenNextAttemptWouldPassWindow()
    {
        var attempts = 0;
        var result = await ReconnectScheduler.RunAsync(
            (_, _) => { attempts++; return Task.FromResult(ReconnectAttemptOutcome.RetryLater); },
            TimeSpan.FromMilliseconds(250), [TimeSpan.FromMilliseconds(100)]);

        Assert.False(result);
        Assert.InRange(attempts, 2, 3);
    }

    [Fact]
    public async Task Scheduler_CancellationStopsRetrying()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        var run = ReconnectScheduler.RunAsync(
            (_, _) => { attempts++; cts.Cancel(); return Task.FromResult(ReconnectAttemptOutcome.RetryLater); },
            TimeSpan.FromSeconds(5), [TimeSpan.FromSeconds(1)], cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, attempts);
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

    private sealed class Student(TcpClientService tcp, SecureSessionChannel secure)
    {
        public TcpClientService Tcp { get; } = tcp;
        public SecureSessionChannel Secure { get; } = secure;
        public Channel<string> Tokens { get; } = Channel.CreateUnbounded<string>();
        public TaskCompletionSource<SessionEndedNotice> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> NextTokenAsync() => await Tokens.Reader.ReadAsync().AsTask().WaitAsync(Wait);

        /// <summary>정상 퇴장 없이 두 연결을 모두 끊습니다(네트워크 단절 흉내).</summary>
        public async Task DropAsync()
        {
            Tcp.Dispose();
            await Secure.DisposeAsync();
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<Student> _students = new();
        private readonly PacketSerializer _serializer = new();
        private int _disposed;

        public X509Certificate2 Certificate { get; } = ProfessorCertificateStore.CreateEphemeral();
        public SessionManager SessionManager { get; }
        public InMemoryLogSink Log { get; } = new();
        public int Port { get; private set; }

        private Rig()
        {
            SessionManager = new SessionManager(Log, new TcpServerService(Log, new PacketSerializer()));
        }

        public static async Task<Rig> OpenAsync(string roomPassword = "")
        {
            var rig = new Rig();
            for (var attempt = 0; ; attempt++)
            {
                rig.Port = TestPortAllocator.GetFreePortPair();
                try
                {
                    await rig.SessionManager.OpenSessionAsync("ReconnectTest", rig.Port, roomPassword.AsMemory(), rig.Certificate);
                    return rig;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 5) { }
            }
        }

        public Task<SecureSessionChannel> AuthenticateAsync(string displayName, string password = "", string? reconnectToken = null) =>
            SecureRoomJoinClient.AuthenticateAsync("127.0.0.1", Port, SessionManager.ConnectionCode!, displayName,
                password.AsMemory(), new InMemoryLogSink(), Wait, reconnectToken: reconnectToken);

        public async Task<Student> JoinAsync(string displayName, string password = "", string? reconnectToken = null)
        {
            var secure = await AuthenticateAsync(displayName, password, reconnectToken);
            var tcp = new TcpClientService(new InMemoryLogSink(), _serializer);
            var student = new Student(tcp, secure);
            secure.FrameReceived += frame =>
            {
                var kind = CollaborationFrameInspector.PeekKind(frame);
                if (kind == CollaborationMessageKind.ReconnectGrant)
                    student.Tokens.Writer.TryWrite(CollaborationMessageCodec.Decode<ReconnectGrantNotice>(frame, out _).Token);
                else if (kind == CollaborationMessageKind.SessionEnded)
                    student.Ended.TrySetResult(CollaborationMessageCodec.Decode<SessionEndedNotice>(frame, out _));
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
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
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
