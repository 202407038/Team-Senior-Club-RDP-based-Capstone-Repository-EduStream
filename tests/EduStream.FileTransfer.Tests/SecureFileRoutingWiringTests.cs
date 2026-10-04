using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.Factories;
using EduStream.Core.FileSharing;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당(U08): 참가 인증을 마친 보호 채널이 파일 라우터에 연결되어, 학생이 목록을 받고 골라 내려받고
/// 교수자가 저장 완료를 확인하는 전체 흐름을 로컬 TLS/TCP로 검증합니다.
/// </summary>
public sealed partial class SecureFileRoutingWiringTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Join_ReceivesCatalog_AndRegisterIsPublished()
    {
        await using var rig = await Rig.OpenAsync();
        var first = await rig.SessionManager.RegisterFileAsync(await rig.WriteSourceAsync(100));
        var alice = await rig.JoinAsync("Alice");

        await WaitUntilAsync(() => alice.Files.Catalog?.Files.Count == 1);
        Assert.Equal(first.FileId, alice.Files.Catalog!.Files.Single().FileId);

        var second = await rig.SessionManager.RegisterFileAsync(await rig.WriteSourceAsync(200));
        await WaitUntilAsync(() => alice.Files.Catalog?.Files.Count == 2);

        Assert.True(rig.SessionManager.UnregisterFile(first.FileId));
        await WaitUntilAsync(() => alice.Files.Catalog?.Files.Count == 1);
        Assert.Equal(second.FileId, alice.Files.Catalog!.Files.Single().FileId);
    }

    [Fact]
    public async Task Download_SavesIdenticalFileAndProfessorSeesStored()
    {
        await using var rig = await Rig.OpenAsync();
        var source = await rig.WriteSourceAsync(FileTransferRules.DefaultChunkSize * 3 + 123);
        var file = await rig.SessionManager.RegisterFileAsync(source);
        var alice = await rig.JoinAsync("Alice");
        await WaitUntilAsync(() => alice.Files.Catalog?.Files.Count == 1);
        var stored = new TaskCompletionSource<(ParticipantConnection, FileStoredNotice)>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.SessionManager.FileTransfers!.FileStored += (student, notice) => stored.TrySetResult((student, notice));

        var receipt = await alice.Files.DownloadAsync(file.FileId).WaitAsync(Wait);

        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(receipt.LocalPath));
        var (student, notice) = await stored.Task.WaitAsync(Wait);
        Assert.Equal("Alice", rig.SessionManager.Participants.TryResolve(student.ConnectionId)!.DisplayName);
        Assert.Equal(file.FileId, notice.FileId);
    }

    [Fact]
    public async Task TwoStudents_DownloadIndependently()
    {
        await using var rig = await Rig.OpenAsync();
        var source = await rig.WriteSourceAsync(FileTransferRules.DefaultChunkSize * 2);
        var file = await rig.SessionManager.RegisterFileAsync(source);
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");
        await WaitUntilAsync(() => alice.Files.Catalog is not null && bob.Files.Catalog is not null);

        var receipts = await Task.WhenAll(alice.Files.DownloadAsync(file.FileId), bob.Files.DownloadAsync(file.FileId)).WaitAsync(Wait);

        Assert.All(receipts, receipt => Assert.Equal(file.Sha256, receipt.Sha256, ignoreCase: true));
        Assert.NotEqual(receipts[0].LocalPath, receipts[1].LocalPath);
    }

    [Fact]
    public async Task StudentLeaves_DuringDownload_ServerTransferIsCancelled()
    {
        await using var rig = await Rig.OpenAsync();
        // 큰 파일로 전송이 진행 중인 동안 퇴장시킨다.
        var file = await rig.SessionManager.RegisterFileAsync(await rig.WriteSourceAsync(8 * 1024 * 1024));
        var alice = await rig.JoinAsync("Alice");
        await WaitUntilAsync(() => alice.Files.Catalog is not null);
        var download = alice.Files.DownloadAsync(file.FileId);
        await WaitUntilAsync(() => rig.SessionManager.FileTransfers!.PendingTransferCount == 1 || download.IsCompleted);

        alice.Tcp.Dispose();

        await WaitUntilAsync(() => rig.SessionManager.FileTransfers!.PendingTransferCount == 0);
        await WaitUntilAsync(() => rig.SessionManager.ParticipantCount == 0);
        // 학생 쪽은 보호 채널이 닫히면서 다운로드가 끝나야 한다(성공했다면 이미 저장 완료된 것).
        alice.Files.ConnectionClosed();
        try { await download.WaitAsync(Wait); } catch (CollaborationException) { }
    }

    [Fact]
    public async Task FileFramesBeforeJoin_AreNotRouted()
    {
        await using var rig = await Rig.OpenAsync();
        var file = await rig.SessionManager.RegisterFileAsync(await rig.WriteSourceAsync(64));
        var secure = await SecureRoomJoinClient.AuthenticateAsync("127.0.0.1", rig.Port, rig.SessionManager.ConnectionCode!,
            "Alice", ReadOnlyMemory<char>.Empty, new InMemoryLogSink(), Wait);
        var received = 0;
        secure.FrameReceived += _ => { Interlocked.Increment(ref received); return Task.CompletedTask; };

        // TCP 참가 전(티켓 미사용)에는 파일 요청을 보내도 라우팅되지 않는다.
        await secure.Connection.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(),
            new SessionFileRequest(Guid.NewGuid(), secure.SessionId, file.FileId, file.Revision)));
        await Task.Delay(300);

        Assert.Equal(0, received);
        Assert.Equal(0, rig.SessionManager.FileTransfers!.PendingTransferCount);
        await secure.DisposeAsync();
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

    private sealed class Student(TcpClientService tcp, SecureSessionChannel secure, SessionFileRequestClient files)
    {
        public TcpClientService Tcp { get; } = tcp;
        public SecureSessionChannel Secure { get; } = secure;
        public SessionFileRequestClient Files { get; } = files;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<Student> _students = new();
        private readonly PacketSerializer _serializer = new();

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "EduStream-secure-file-" + Guid.NewGuid().ToString("N"));
        public X509Certificate2 Certificate { get; } = ProfessorCertificateStore.CreateEphemeral();
        public SessionManager SessionManager { get; }
        public int Port { get; private set; }

        private Rig()
        {
            Directory.CreateDirectory(Root);
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
                    await rig.SessionManager.OpenSessionAsync("SecureFileTest", rig.Port, default, rig.Certificate);
                    return rig;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && attempt < 5) { }
            }
        }

        public async Task<string> WriteSourceAsync(int length)
        {
            var path = Path.Combine(Root, "source-" + Guid.NewGuid().ToString("N") + ".bin");
            var content = new byte[length];
            new Random(length).NextBytes(content);
            await File.WriteAllBytesAsync(path, content);
            return path;
        }

        public async Task<Student> JoinAsync(string displayName,
            Func<ISessionFileDownloader, ISessionFileDownloader>? decorateDownloader = null)
        {
            var secure = await SecureRoomJoinClient.AuthenticateAsync("127.0.0.1", Port, SessionManager.ConnectionCode!,
                displayName, ReadOnlyMemory<char>.Empty, new InMemoryLogSink(), Wait);
            var downloads = Path.Combine(Root, "downloads-" + displayName);
            ISessionFileDownloader downloader = new SessionFileDownloader(new ReadinessDirectory(downloads));
            var files = new SessionFileRequestClient(secure.SessionId, secure.Connection,
                decorateDownloader?.Invoke(downloader) ?? downloader, new InMemoryLogSink());
            secure.FrameReceived += frame => files.HandleFrameAsync(frame);
            var tcp = new TcpClientService(new InMemoryLogSink(), _serializer);
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
            var student = new Student(tcp, secure, files);
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
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
