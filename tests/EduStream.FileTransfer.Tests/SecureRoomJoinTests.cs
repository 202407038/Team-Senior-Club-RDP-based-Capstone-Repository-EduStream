using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
/// 2번 담당(U03): 보호 채널 참가 인증 → 일회용 티켓 → 기존 TCP 참가 흐름과, 두 연결의 수명 연동을
/// 로컬 루프백의 실제 TLS/TCP로 검증합니다.
/// </summary>
public sealed class SecureRoomJoinTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task NoPasswordRoom_CodeThenTicket_Joins()
    {
        await using var rig = await Rig.OpenAsync();
        Assert.True(rig.SessionManager.IsSecureJoinRequired);
        Assert.Equal(ConnectionCode.FromCertificate(rig.Certificate), rig.SessionManager.ConnectionCode);

        await using var secure = await rig.AuthenticateAsync("Alice");
        var response = await rig.JoinAsync("Alice", secure.JoinTicket);

        Assert.Equal(AckCodes.SessionJoined, Assert.IsType<AckPacket>(response).AckCode);
        Assert.Equal(rig.SessionManager.CurrentSession!.SessionId, secure.SessionId);
        Assert.Equal(1, rig.SessionManager.SecureConnectionCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("forged-ticket")]
    public async Task SecureSession_RejectsJoinWithoutValidTicket(string? ticket)
    {
        await using var rig = await Rig.OpenAsync();

        var response = await rig.JoinAsync("Alice", ticket);

        Assert.Equal(ErrorCodes.JoinRejected, Assert.IsType<ErrorPacket>(response).ErrorCode);
        Assert.Equal(0, rig.SessionManager.ParticipantCount);
    }

    [Fact]
    public async Task PasswordRoom_CorrectPasswordJoins_WrongPasswordRejected()
    {
        await using var rig = await Rig.OpenAsync("room-pass");

        var wrong = await Assert.ThrowsAsync<SecureJoinException>(() => rig.AuthenticateAsync("Alice", "nope"));
        Assert.Equal(SecureJoinFailure.PasswordRejected, wrong.Failure);

        await using var secure = await rig.AuthenticateAsync("Alice", "room-pass");
        var response = await rig.JoinAsync("Alice", secure.JoinTicket);
        Assert.Equal(AckCodes.SessionJoined, Assert.IsType<AckPacket>(response).AckCode);
    }

    [Fact]
    public async Task PasswordRoom_RepeatedFailures_LockOut()
    {
        await using var rig = await Rig.OpenAsync("room-pass");
        for (var i = 0; i < RoomPasswordVerifier.MaxFailedAttempts; i++)
            await Assert.ThrowsAsync<SecureJoinException>(() => rig.AuthenticateAsync("Alice", "nope"));

        var locked = await Assert.ThrowsAsync<SecureJoinException>(() => rig.AuthenticateAsync("Alice", "room-pass"));

        Assert.Equal(SecureJoinFailure.LockedOut, locked.Failure);
    }

    [Fact]
    public async Task Ticket_IsSingleUse()
    {
        await using var rig = await Rig.OpenAsync();
        await using var secure = await rig.AuthenticateAsync("Alice");
        Assert.IsType<AckPacket>(await rig.JoinAsync("Alice", secure.JoinTicket));

        var replay = await rig.JoinAsync("Alice", secure.JoinTicket);

        Assert.Equal(ErrorCodes.JoinRejected, Assert.IsType<ErrorPacket>(replay).ErrorCode);
        Assert.Equal(1, rig.SessionManager.ParticipantCount);
    }

    [Fact]
    public async Task Ticket_IsBoundToDisplayNameAndConsumedOnMismatch()
    {
        await using var rig = await Rig.OpenAsync();
        await using var secure = await rig.AuthenticateAsync("Alice");

        var otherName = await rig.JoinAsync("Mallory", secure.JoinTicket);
        var afterMismatch = await rig.JoinAsync("Alice", secure.JoinTicket);

        Assert.Equal(ErrorCodes.JoinRejected, Assert.IsType<ErrorPacket>(otherName).ErrorCode);
        Assert.Equal(ErrorCodes.JoinRejected, Assert.IsType<ErrorPacket>(afterMismatch).ErrorCode);
        Assert.Equal(0, rig.SessionManager.ParticipantCount);
    }

    [Fact]
    public async Task InvalidIp_FailsBeforeSendingPassword()
    {
        await using var rig = await Rig.OpenAsync("room-pass");

        var error = await Assert.ThrowsAsync<SecureJoinException>(() => SecureRoomJoinClient.AuthenticateAsync(
            "not-an-ip", rig.Port, "Alice", "room-pass".AsMemory(),
            new InMemoryLogSink(), Wait));

        Assert.Equal(SecureJoinFailure.InvalidAddress, error.Failure);
        Assert.DoesNotContain(rig.Log.Snapshot(), line => line.Contains("참가 인증"));
    }

    [Fact]
    public async Task StudentLeavesTcp_ClosesSecureChannel()
    {
        await using var rig = await Rig.OpenAsync();
        await using var secure = await rig.AuthenticateAsync("Alice");
        var client = await rig.ConnectAsync();
        await rig.SendJoinAsync(client, "Alice", secure.JoinTicket);

        client.Dispose();

        await secure.Connection.Completion.WaitAsync(Wait);
        await WaitUntilAsync(() => rig.SessionManager.SecureConnectionCount == 0);
    }

    [Fact]
    public async Task SecureChannelCloses_DisconnectsParticipant()
    {
        await using var rig = await Rig.OpenAsync();
        var secure = await rig.AuthenticateAsync("Alice");
        Assert.IsType<AckPacket>(await rig.JoinAsync("Alice", secure.JoinTicket));
        Assert.Equal(1, rig.SessionManager.ParticipantCount);

        await secure.DisposeAsync();

        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 0);
        Assert.Equal(0, rig.SessionManager.SecureConnectionCount);
    }

    [Fact]
    public async Task CloseSession_ClosesSecureChannelsAndStopsListener()
    {
        var rig = await Rig.OpenAsync();
        await using var secure = await rig.AuthenticateAsync("Alice");
        Assert.IsType<AckPacket>(await rig.JoinAsync("Alice", secure.JoinTicket));

        await rig.DisposeAsync();

        await secure.Connection.Completion.WaitAsync(Wait);
        Assert.Null(rig.SessionManager.ConnectionCode);
        var error = await Assert.ThrowsAsync<SecureJoinException>(() => SecureRoomJoinClient.AuthenticateAsync(
            "127.0.0.1", rig.Port, "Bob", ReadOnlyMemory<char>.Empty, new InMemoryLogSink(), Wait));
        Assert.Equal(SecureJoinFailure.Unreachable, error.Failure);
    }

    [Fact]
    public async Task SecurePortInUse_FailsOpenAndLeavesSessionClosed()
    {
        var port = TestPortAllocator.GetFreePortPair();
        using var blocker = new TcpListener(IPAddress.Any, CollaborationPorts.ForSession(port));
        blocker.Start();
        using var certificate = ProfessorCertificateStore.CreateEphemeral();
        var log = new InMemoryLogSink();
        var sessionManager = new SessionManager(log, new TcpServerService(log, new PacketSerializer()));

        await Assert.ThrowsAsync<SocketException>(() =>
            sessionManager.OpenSessionAsync("SecureJoinTest", port, default, certificate));

        Assert.False(sessionManager.IsSessionOpen);
        Assert.Null(sessionManager.ConnectionCode);
    }

    [Fact]
    public async Task Gate_ClosesConnectionThatNeverAuthenticates()
    {
        await using var gateRig = await GateRig.StartAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100));
        await using var connection = await gateRig.ConnectAsync();
        connection.Start(_ => Task.CompletedTask);

        await connection.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task Gate_ClosesConnectionWhoseFirstFrameIsNotAuth()
    {
        await using var gateRig = await GateRig.StartAsync();
        await using var connection = await gateRig.ConnectAsync();
        connection.Start(_ => Task.CompletedTask);

        await connection.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(),
            new FileCancelRequest(Guid.NewGuid(), Guid.NewGuid())));

        await connection.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task Gate_ExpiredTicketIsRejected()
    {
        await using var gateRig = await GateRig.StartAsync(ticketLifetime: TimeSpan.FromMilliseconds(100));
        await using var secure = await SecureRoomJoinClient.AuthenticateAsync("127.0.0.1", gateRig.SessionPort,
            "Alice", ReadOnlyMemory<char>.Empty, new InMemoryLogSink(), Wait);

        await Task.Delay(300);

        Assert.Null(gateRig.Gate.Redeem(secure.JoinTicket, "Alice"));
        Assert.Equal(0, gateRig.Gate.PendingTicketCount);
    }

    [Fact]
    public void AuthRequest_ToStringHidesPassword()
    {
        var request = new RoomAuthRequest
        {
            AttemptId = Guid.NewGuid(), DisplayName = "Alice", Password = Encoding.UTF8.GetBytes("secret-pass")
        };

        Assert.DoesNotContain("secret", request.ToString());
        Assert.DoesNotContain(Convert.ToBase64String(request.Password), request.ToString());
    }

    [Theory]
    [InlineData(true, null, null)]         // 승인인데 티켓 없음
    [InlineData(false, null, null)]        // 거부인데 사유 없음
    [InlineData(false, "ticket", CollaborationError.NotAuthorized)] // 거부인데 티켓 있음
    public void AuthResult_InconsistentShapesAreRejected(bool accepted, string? ticket, CollaborationError? error)
    {
        var result = new RoomAuthResult(Guid.NewGuid(), accepted, error, ticket, accepted ? Guid.NewGuid() : null);

        Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Encode(Guid.NewGuid(), result));
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

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<TcpClientService> _clients = new();
        private readonly PacketSerializer _serializer = new();
        private int _disposed;

        public X509Certificate2 Certificate { get; } = ProfessorCertificateStore.CreateEphemeral();
        public InMemoryLogSink Log { get; } = new();
        public SessionManager SessionManager { get; }
        public int Port { get; private set; }

        private Rig()
        {
            SessionManager = new SessionManager(Log, new TcpServerService(Log, new PacketSerializer()));
        }

        public static async Task<Rig> OpenAsync(string? roomPassword = null)
        {
            var rig = new Rig();
            // 병렬 테스트가 확인 직후의 포트를 가져갈 수 있어 충돌하면 새 포트 쌍으로 다시 연다.
            for (var attempt = 0; ; attempt++)
            {
                rig.Port = TestPortAllocator.GetFreePortPair();
                try
                {
                    await rig.SessionManager.OpenSessionAsync("SecureJoinTest", rig.Port,
                        (roomPassword ?? string.Empty).AsMemory(), rig.Certificate);
                    return rig;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 5) { }
            }
        }

        public Task<SecureSessionChannel> AuthenticateAsync(string displayName, string password = "") =>
            SecureRoomJoinClient.AuthenticateAsync("127.0.0.1", Port,
                displayName, password.AsMemory(), new InMemoryLogSink(), Wait);

        public async Task<TcpClientService> ConnectAsync()
        {
            var client = new TcpClientService(new InMemoryLogSink(), _serializer);
            await client.ConnectAsync("127.0.0.1", Port);
            _clients.Add(client);
            return client;
        }

        public Task SendJoinAsync(TcpClientService client, string displayName, string? ticket) =>
            client.SendAsync(PacketFactory.CreateSessionJoin(displayName, displayName, "127.0.0.1", Port, ticket));

        /// <summary>새 TCP 연결로 참가 요청을 보내고 첫 Ack/Error 응답을 돌려줍니다.</summary>
        public async Task<BasePacket> JoinAsync(string displayName, string? ticket)
        {
            var client = await ConnectAsync();
            var response = new TaskCompletionSource<BasePacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PacketReceived += (type, payload) =>
            {
                if (type == PacketType.Ack) response.TrySetResult(_serializer.Deserialize<AckPacket>(payload)!);
                else if (type == PacketType.Error) response.TrySetResult(_serializer.Deserialize<ErrorPacket>(payload)!);
                return Task.CompletedTask;
            };
            await SendJoinAsync(client, displayName, ticket);
            return await response.Task.WaitAsync(Wait);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var client in _clients)
            {
                try { client.Dispose(); } catch { }
            }
            await SessionManager.CloseSessionAsync();
            Certificate.Dispose();
        }
    }

    /// <summary>SessionManager 없이 리스너와 게이트만 조립해 시간 제한을 짧게 검증합니다.</summary>
    private sealed class GateRig : IAsyncDisposable
    {
        private readonly X509Certificate2 _certificate = ProfessorCertificateStore.CreateEphemeral();

        public SecureCollaborationListener Listener { get; }
        public SecureRoomGate Gate { get; }
        public int SessionPort => Listener.Port - CollaborationPorts.Offset;

        private GateRig(TimeSpan? authTimeout, TimeSpan? ticketLifetime)
        {
            var log = new InMemoryLogSink();
            Listener = new SecureCollaborationListener(_certificate, log);
            Gate = new SecureRoomGate(Listener, Guid.NewGuid(), null, log, authTimeout, ticketLifetime);
        }

        public static Task<GateRig> StartAsync(TimeSpan? authTimeout = null, TimeSpan? ticketLifetime = null)
        {
            var rig = new GateRig(authTimeout, ticketLifetime);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    rig.Listener.Start(CollaborationPorts.ForSession(TestPortAllocator.GetFreePortPair()));
                    return Task.FromResult(rig);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 5) { }
            }
        }

        public Task<SecureCollaborationConnection> ConnectAsync() =>
            SecureCollaborationConnector.ConnectAsync("127.0.0.1", Listener.Port,
                new InMemoryLogSink(), Wait);

        public async ValueTask DisposeAsync()
        {
            await Gate.DisposeAsync();
            await Listener.DisposeAsync();
            _certificate.Dispose();
        }
    }
}
