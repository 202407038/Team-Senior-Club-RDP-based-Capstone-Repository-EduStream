using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using EduStream.Client.Services;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 9월 4주차 2번: 방 비밀번호 지정/미설정, 해시 검증·시도 제한, 접속용 호스트 IP 정보를 검증합니다.
/// 보호 채널이 없는 동안 비밀번호 방이 평문 참가 요청으로 열리지 않는지(fail-closed)와
/// TCP 연결만 한 미참가 연결이 강의 데이터를 받거나 기능을 요청하지 못하는지도 확인합니다.
/// </summary>
public sealed class RoomPasswordAndHostNetworkTests
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    [Fact]
    public void Verifier_EmptyPassword_MeansNoPasswordRoom()
    {
        Assert.Null(RoomPasswordVerifier.Create(ReadOnlySpan<char>.Empty));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(null)]
    public void Verifier_RejectsBlankOrTooLongPassword(string? password)
    {
        var value = password ?? new string('a', RoomPasswordVerifier.MaxPasswordLength + 1);
        Assert.Throws<ArgumentException>(() => RoomPasswordVerifier.Create(value));
    }

    [Fact]
    public void Verifier_AcceptsOnlyExactPassword()
    {
        var verifier = RoomPasswordVerifier.Create("secret1234")!;

        Assert.Equal(RoomPasswordResult.Accepted, verifier.Verify("10.0.0.2", "secret1234"));
        Assert.Equal(RoomPasswordResult.Rejected, verifier.Verify("10.0.0.2", "Secret1234"));
        Assert.Equal(RoomPasswordResult.Rejected, verifier.Verify("10.0.0.2", "secret1234 "));
        Assert.Equal(RoomPasswordResult.Rejected, verifier.Verify("10.0.0.2", ""));
    }

    [Fact]
    public void Verifier_LocksOutAfterRepeatedFailures_ThenRecovers()
    {
        var clock = new ManualTimeProvider();
        var verifier = RoomPasswordVerifier.Create("secret1234", clock)!;

        for (var i = 0; i < RoomPasswordVerifier.MaxFailedAttempts; i++)
            Assert.Equal(RoomPasswordResult.Rejected, verifier.Verify("10.0.0.2", "wrong"));

        // 잠금 중에는 맞는 비밀번호도 받지 않는다. 다른 시도 키는 영향이 없다.
        Assert.Equal(RoomPasswordResult.LockedOut, verifier.Verify("10.0.0.2", "secret1234"));
        Assert.Equal(RoomPasswordResult.Accepted, verifier.Verify("10.0.0.3", "secret1234"));

        clock.Advance(RoomPasswordVerifier.LockoutDuration + TimeSpan.FromSeconds(1));
        Assert.Equal(RoomPasswordResult.Accepted, verifier.Verify("10.0.0.2", "secret1234"));
    }

    [Fact]
    public async Task NoPasswordRoom_LegacyJoinSucceeds()
    {
        await using var rig = await Rig.OpenAsync();

        var response = await rig.ConnectAndJoinAsync("Alice");

        Assert.False(rig.SessionManager.IsRoomPasswordProtected);
        Assert.Equal(PacketType.Ack, response);
        Assert.Equal(1, rig.SessionManager.ParticipantCount);
    }

    [Fact]
    public async Task PasswordRoom_RejectsPlaintextLegacyJoin()
    {
        await using var rig = await Rig.OpenAsync("secret1234");

        var response = await rig.ConnectAndJoinAsync("Alice");

        Assert.True(rig.SessionManager.IsRoomPasswordProtected);
        Assert.Equal(PacketType.Error, response);
        Assert.Equal(0, rig.SessionManager.ParticipantCount);
    }

    [Fact]
    public async Task PasswordRoom_NeverLogsPasswordValue()
    {
        await using var rig = await Rig.OpenAsync("secret1234");
        await rig.ConnectAndJoinAsync("Alice");

        Assert.DoesNotContain(rig.Log.Snapshot(), line => line.Contains("secret1234"));
    }

    [Fact]
    public async Task CloseSession_ClearsRoomPassword()
    {
        var rig = await Rig.OpenAsync("secret1234");
        await rig.DisposeAsync();

        Assert.False(rig.SessionManager.IsRoomPasswordProtected);
    }

    [Fact]
    public async Task UnjoinedConnection_DoesNotReceiveProfessorChat()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.ConnectRecorderAsync(join: "Alice");
        var unjoined = await rig.ConnectRecorderAsync(join: null);

        await rig.SessionManager.BroadcastPacketAsync(
            PacketFactory.CreateChat(senderId: "Server", sender: "교수자", message: "강의 공지"));

        await WaitUntilAsync(() => alice.Count(PacketType.Chat) == 1, DefaultWait); // 출입은 교수자 기록에만, 공지만 채팅 전송
        await Task.Delay(200);
        Assert.Equal(0, unjoined.Count(PacketType.Chat));
    }

    [Fact]
    public async Task AnnouncementReachesParticipantButStudentCannotForgeAnnouncement()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.ConnectRecorderAsync(join: "Alice");
        var unjoined = await rig.ConnectRecorderAsync(join: null);
        await rig.SessionManager.BroadcastPacketAsync(new ChatPacket
        { Sender = "Professor", Message = "real-notice", IsAnnouncement = true });
        await WaitUntilAsync(() => alice.Chat("real-notice") is not null, DefaultWait);
        Assert.True(alice.Chat("real-notice")!.IsAnnouncement);
        Assert.Null(unjoined.Chat("real-notice"));

        await alice.Client.SendAsync(new ChatPacket
        { Sender = "Professor", Message = "fake-notice", IsAnnouncement = true, IsSystemMessage = true,
            IsImportantAnnouncement = true, IsAnnouncementHistory = true, AnnouncementId = Guid.NewGuid() });
        await WaitUntilAsync(() => alice.Chat("fake-notice") is not null, DefaultWait);
        var relayed = alice.Chat("fake-notice")!;
        Assert.Equal("Alice", relayed.Sender);
        Assert.False(relayed.IsAnnouncement);
        Assert.False(relayed.IsSystemMessage);
        Assert.False(relayed.IsImportantAnnouncement);
        Assert.False(relayed.IsAnnouncementHistory);
        Assert.Null(relayed.AnnouncementId);
    }

    [Fact]
    public async Task AnnouncementsReplayToLateJoinerAndReceiptsDoNotLeakToChat()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.ConnectRecorderAsync(join: "Alice");
        var notice = new ChatPacket { IsAnnouncement = true, IsImportantAnnouncement = true, Message = "persistent-notice" };
        await rig.SessionManager.BroadcastPacketAsync(notice);
        await WaitUntilAsync(() => alice.Chat("persistent-notice") is not null, DefaultWait);
        Assert.Equal(0, rig.SessionManager.Announcements.Snapshot()[0].Received);
        var bob = await rig.ConnectRecorderAsync(join: "Bob");
        await WaitUntilAsync(() => bob.Chat("persistent-notice") is not null, DefaultWait);
        Assert.True(bob.Chat("persistent-notice")!.IsAnnouncementHistory);
        await alice.Client.SendAsync(new ChatPacket { SessionId = notice.SessionId, AnnouncementId = notice.AnnouncementId,
            AnnouncementReceipt = AnnouncementReceipt.Confirmed, Sender = "Bob", Message = "receipt-not-chat" });
        await WaitUntilAsync(() => rig.SessionManager.Announcements.Snapshot()[0].Confirmed == 1, DefaultWait);
        Assert.Contains("Alice: 확인", rig.SessionManager.Announcements.Snapshot()[0].Details);
        Assert.Contains("Bob: 수신 미확인", rig.SessionManager.Announcements.Snapshot()[0].Details);
        Assert.Null(bob.Chat("receipt-not-chat"));
    }

    [Fact]
    public async Task PasswordRoom_UnjoinedConnection_ReceivesNoLectureData()
    {
        // 리뷰 재현: 비밀번호 방에 TCP 연결만 하고 참가/비밀번호를 보내지 않은 연결.
        await using var rig = await Rig.OpenAsync("secret1234");
        var unjoined = await rig.ConnectRecorderAsync(join: null);

        await rig.SessionManager.BroadcastPacketAsync(
            PacketFactory.CreateChat(senderId: "Server", sender: "교수자", message: "강의 공지"));
        await rig.SessionManager.BroadcastPacketAsync(PacketFactory.CreateScreenFrame(
            senderId: "Server", frameIndex: 1, frameDescription: "test", width: 2, height: 2,
            encoding: ScreenEncodings.Png, content: new byte[] { 1, 2, 3 }));

        await Task.Delay(300);
        Assert.Equal(0, unjoined.Count(PacketType.Chat));
        Assert.Equal(0, unjoined.Count(PacketType.Screen));
    }

    [Fact]
    public async Task UnjoinedConnection_FilePacket_IsRejectedAndNotRelayed()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.ConnectRecorderAsync(join: "Alice");
        var unjoined = await rig.ConnectRecorderAsync(join: null);

        await unjoined.Client.SendAsync(PacketFactory.CreateFileChunk(
            senderId: "ghost", fileName: "a.bin", fileSize: 3, checksum: new string('0', 64),
            transferId: Guid.NewGuid(), chunkIndex: 0, totalChunks: 1, content: new byte[] { 1, 2, 3 }));

        await WaitUntilAsync(() => unjoined.Count(PacketType.Error) >= 1, DefaultWait);
        Assert.Equal(ErrorCodes.NotParticipant, unjoined.LastErrorCode());
        await Task.Delay(200);
        Assert.Equal(0, alice.Count(PacketType.File));
    }

    [Fact]
    public async Task UnjoinedConnection_ScreenPacket_IsRejectedAndNotRelayed()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.ConnectRecorderAsync(join: "Alice");
        var unjoined = await rig.ConnectRecorderAsync(join: null);

        await unjoined.Client.SendAsync(PacketFactory.CreateScreenFrame(
            senderId: "ghost", frameIndex: 1, frameDescription: "fake", width: 2, height: 2,
            encoding: ScreenEncodings.Png, content: new byte[] { 1, 2, 3 }));

        await WaitUntilAsync(() => unjoined.Count(PacketType.Error) >= 1, DefaultWait);
        Assert.Equal(ErrorCodes.NotParticipant, unjoined.LastErrorCode());
        await Task.Delay(200);
        Assert.Equal(0, alice.Count(PacketType.Screen));
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Loopback, "Loopback", "", "127.0.0.1", HostAddressKind.Loopback)]
    [InlineData(NetworkInterfaceType.Ethernet, "이더넷", "Realtek PCIe GbE", "169.254.10.2", HostAddressKind.LinkLocal)]
    [InlineData(NetworkInterfaceType.Ethernet, "이더넷", "Realtek PCIe GbE", "192.168.0.10", HostAddressKind.Lan)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Intel Wi-Fi 6", "192.168.0.11", HostAddressKind.Wireless)]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", "172.20.0.1", HostAddressKind.Virtual)]
    [InlineData(NetworkInterfaceType.Ethernet, "OpenVPN", "TAP-Windows Adapter V9", "10.8.0.2", HostAddressKind.Vpn)]
    [InlineData(NetworkInterfaceType.Ppp, "회사 VPN", "WAN Miniport", "10.9.0.2", HostAddressKind.Vpn)]
    public void HostNetwork_ClassifiesAddressKind(NetworkInterfaceType type, string name, string description,
        string address, HostAddressKind expected)
    {
        Assert.Equal(expected, HostNetworkInfoService.Classify(type, name, description, IPAddress.Parse(address)));
    }

    [Fact]
    public void HostNetwork_OnlyLanWirelessVpnAreJoinCandidates()
    {
        Assert.True(new HostAddressInfo("이더넷", "192.168.0.10", HostAddressKind.Lan).IsJoinCandidate);
        Assert.True(new HostAddressInfo("VPN", "10.8.0.2", HostAddressKind.Vpn).IsJoinCandidate);
        Assert.False(new HostAddressInfo("Loopback", "127.0.0.1", HostAddressKind.Loopback).IsJoinCandidate);
        Assert.False(new HostAddressInfo("vEthernet", "172.20.0.1", HostAddressKind.Virtual).IsJoinCandidate);
    }

    [Fact]
    public void HostNetwork_FormatsEndpointForCopy()
    {
        Assert.Equal("192.168.0.10:5000", HostNetworkInfoService.FormatEndpoint("192.168.0.10", 5000));
        Assert.Equal("[fe80::1]:5000", HostNetworkInfoService.FormatEndpoint("fe80::1", 5000));
        Assert.Throws<ArgumentOutOfRangeException>(() => HostNetworkInfoService.FormatEndpoint("192.168.0.10", 0));
    }

    [Fact]
    public void HostNetwork_ListsOnlyIpv4InCandidateOrder()
    {
        var addresses = new HostNetworkInfoService(new InMemoryLogSink()).GetAddresses();

        Assert.All(addresses, info => Assert.Equal(AddressFamily.InterNetwork, IPAddress.Parse(info.Address).AddressFamily));
        Assert.Equal(addresses.OrderBy(info => info.Kind).Select(info => info.Kind), addresses.Select(info => info.Kind));
    }

    [Fact]
    public async Task DirectChat_OnlySelectedStudentReceivesAndStudentIdentityIsBoundToConnection()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.ConnectRecorderAsync("Alice", directChat: true);
        var bob = await rig.ConnectRecorderAsync("Bob", directChat: true);
        var old = await rig.ConnectRecorderAsync("Old");
        var a = Assert.Single(rig.SessionManager.DirectChatRecipients, x => x.Name == "Alice");
        Assert.DoesNotContain(rig.SessionManager.DirectChatRecipients, x => x.Name == "Old");
        await rig.SessionManager.SendDirectChatAsync(a.ConnectionId, "Only Alice");
        await WaitUntilAsync(() => alice.Count(PacketType.DirectChat) == 1, DefaultWait);
        Assert.True(alice.Direct()!.FromProfessor);
        DirectChatPacket? received = null;
        rig.SessionManager.DirectChatReceived += packet => received = packet;
        await alice.Client.SendAsync(new DirectChatPacket
        {
            SessionId = rig.SessionManager.CurrentSession!.SessionId, StudentConnectionId = a.ConnectionId,
            FromProfessor = true, Sender = "Bob", Message = "Only professor"
        });
        await WaitUntilAsync(() => alice.Count(PacketType.DirectChat) == 2, DefaultWait);
        Assert.NotNull(received);
        Assert.Equal("Alice", received.Sender);
        Assert.False(received.FromProfessor);
        Assert.Equal(a.ConnectionId, received.StudentConnectionId);
        await Task.Delay(150);
        Assert.Equal(0, bob.Count(PacketType.DirectChat));
        Assert.Equal(0, old.Count(PacketType.DirectChat));
        Assert.Null(bob.Chat("Only professor"));
        Assert.Null(old.Chat("Only Alice"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.SessionManager.SendDirectChatAsync(Guid.NewGuid(), "gone"));
    }

    [Fact]
    public async Task DirectChat_RejectsUnjoinedOldClientWrongSessionAndOtherStudentsConnection()
    {
        await using var rig = await Rig.OpenAsync();
        var alice = await rig.ConnectRecorderAsync("Alice", directChat: true);
        var bob = await rig.ConnectRecorderAsync("Bob", directChat: true);
        var old = await rig.ConnectRecorderAsync("Old");
        var ghost = await rig.ConnectRecorderAsync(null);
        int deliveries = 0;
        rig.SessionManager.DirectChatReceived += _ => Interlocked.Increment(ref deliveries);
        var current = rig.SessionManager.CurrentSession!.SessionId;
        var b = rig.SessionManager.DirectChatRecipients.Single(x => x.Name == "Bob");
        foreach (var item in new[] { (old, current, Guid.Empty), (ghost, current, Guid.Empty),
                     (alice, Guid.NewGuid(), Guid.Empty), (alice, current, b.ConnectionId) })
        {
            int errors = item.Item1.Count(PacketType.Error);
            await item.Item1.Client.SendAsync(new DirectChatPacket
            { SessionId = item.Item2, StudentConnectionId = item.Item3, Message = "must reject" });
            await WaitUntilAsync(() => item.Item1.Count(PacketType.Error) > errors, DefaultWait);
            Assert.Equal("DIRECT_CHAT_REJECTED", item.Item1.LastErrorCode());
        }
        Assert.Equal(0, deliveries);
        Assert.Equal(0, bob.Count(PacketType.DirectChat));
    }

    [Fact]
    public async Task RosterAdmission_RejectsMismatchAllowsTwoSameNamesWithDifferentIdsAndRejectsDuplicate()
    {
        await using var rig = await Rig.OpenAsync(roster: [new(2, "학생 A", "001", "", ""), new(3, "학생 A", "002", "", "")]);
        var one = await rig.ConnectRecorderAsync(null);
        var two = await rig.ConnectRecorderAsync(null);
        var duplicate = await rig.ConnectRecorderAsync(null);
        var rejected = await rig.ConnectRecorderAsync(null);
        var mismatch = PacketFactory.CreateSessionJoin("student", "학생 A", "127.0.0.1", rig.Port);
        mismatch.RosterName = "학생 A"; mismatch.StudentId = "001"; // canonical connection identity omitted
        await rejected.Client.SendAsync(mismatch);
        await WaitUntilAsync(() => rejected.Count(PacketType.Error) > 0, DefaultWait);
        Assert.Equal("ROSTER_MISMATCH", rejected.LastErrorCode());
        foreach (var pair in new[] { (one, "001"), (two, "002"), (duplicate, "001") })
        {
            var request = PacketFactory.CreateSessionJoin("student", RosterIdentity.ConnectionName("학생 A", pair.Item2), "127.0.0.1", rig.Port);
            request.RosterName = "학생 A"; request.StudentId = pair.Item2;
            await pair.Item1.Client.SendAsync(request);
            await WaitUntilAsync(() => pair.Item1.Count(PacketType.Ack) + pair.Item1.Count(PacketType.Error) > 0, DefaultWait);
        }
        Assert.Equal(1, one.Count(PacketType.Ack));
        Assert.Equal(1, two.Count(PacketType.Ack));
        Assert.Equal(ErrorCodes.AlreadyJoined, duplicate.LastErrorCode());
        Assert.Equal(2, rig.SessionManager.ParticipantCount);
        Assert.Throws<InvalidOperationException>(() => rig.SessionManager.ConfigureRosterAdmission([], false));
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
        throw new TimeoutException("조건이 제한 시간 안에 충족되지 않았습니다.");
    }

    private sealed class PacketRecorder(TcpClientService client)
    {
        private readonly ConcurrentQueue<(PacketType Type, byte[] Payload)> _received = new();

        public TcpClientService Client { get; } = client;

        public void Record(PacketType type, byte[] payload) => _received.Enqueue((type, payload));

        public int Count(PacketType type) => _received.Count(p => p.Type == type);
        public DirectChatPacket? Direct() => _received.Where(p => p.Type == PacketType.DirectChat)
            .Select(p => JsonSerializer.Deserialize<DirectChatPacket>(p.Payload)).LastOrDefault();
        public ChatPacket? Chat(string message) => _received.Where(p => p.Type == PacketType.Chat)
            .Select(p => JsonSerializer.Deserialize<ChatPacket>(p.Payload)).LastOrDefault(p => p?.Message == message);

        public string? LastErrorCode() =>
            _received.Where(p => p.Type == PacketType.Error)
                .Select(p => JsonSerializer.Deserialize<ErrorPacket>(p.Payload)?.ErrorCode)
                .LastOrDefault();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<TcpClientService> _clients = new();
        private bool _disposed;

        private Rig(InMemoryLogSink log, SessionManager sessionManager, int port)
        {
            Log = log;
            SessionManager = sessionManager;
            Port = port;
        }

        public InMemoryLogSink Log { get; }
        public SessionManager SessionManager { get; }
        public int Port { get; }

        public static async Task<Rig> OpenAsync(string? roomPassword = null, IReadOnlyList<RosterRow>? roster = null)
        {
            var port = GetFreePort();
            var log = new InMemoryLogSink();
            var sessionManager = new SessionManager(log, new TcpServerService(log, new PacketSerializer()));
            if (roster is not null) sessionManager.ConfigureRosterAdmission(roster, true);
            await sessionManager.OpenSessionAsync("RoomPasswordTest", port, (roomPassword ?? string.Empty).AsMemory());
            return new Rig(log, sessionManager, port);
        }

        public async Task<PacketType> ConnectAndJoinAsync(string displayName)
        {
            var client = new TcpClientService(new InMemoryLogSink(), new PacketSerializer());
            await client.ConnectAsync("127.0.0.1", Port);
            _clients.Add(client);

            var response = new TaskCompletionSource<PacketType>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PacketReceived += (packetType, _) =>
            {
                if (packetType is PacketType.Ack or PacketType.Error)
                    response.TrySetResult(packetType);
                return Task.CompletedTask;
            };
            await client.SendAsync(PacketFactory.CreateSessionJoin(
                senderId: displayName, displayName: displayName, targetAddress: "127.0.0.1", targetPort: Port));
            return await response.Task.WaitAsync(DefaultWait);
        }

        /// <summary>
        /// 수신 패킷을 기록하는 연결을 만듭니다. join이 null이면 TCP 연결만 하고 참가하지 않습니다.
        /// </summary>
        public async Task<PacketRecorder> ConnectRecorderAsync(string? join, bool directChat = false)
        {
            var client = new TcpClientService(new InMemoryLogSink(), new PacketSerializer());
            var recorder = new PacketRecorder(client);
            client.PacketReceived += (packetType, payload) =>
            {
                recorder.Record(packetType, payload);
                return Task.CompletedTask;
            };
            await client.ConnectAsync("127.0.0.1", Port);
            _clients.Add(client);

            if (join is not null)
            {
                var request = PacketFactory.CreateSessionJoin(
                    senderId: join, displayName: join, targetAddress: "127.0.0.1", targetPort: Port);
                request.SupportsDirectChat = directChat;
                await client.SendAsync(request);
                await WaitUntilAsync(() => recorder.Count(PacketType.Ack) >= 1, DefaultWait);
            }
            return recorder;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var client in _clients)
            {
                try { client.Dispose(); } catch { }
            }
            await SessionManager.CloseSessionAsync();
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
