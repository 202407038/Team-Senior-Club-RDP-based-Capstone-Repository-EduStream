using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading.Channels;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;
using EduStream.Core.Network;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당: 보호 채널(TLS + 접속 코드 확인 + capability 협상 + 길이 제한 프레이밍)을 로컬 루프백 실제 TLS로 검증합니다.
/// </summary>
public sealed class SecureCollaborationChannelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void ConnectionCode_IsStableFormattedAndDiffersPerCertificate()
    {
        using var first = ProfessorCertificateStore.CreateEphemeral();
        using var second = ProfessorCertificateStore.CreateEphemeral();

        var code = ConnectionCode.FromCertificate(first);

        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", code);
        Assert.Equal(code, ConnectionCode.FromCertificate(first));
        Assert.NotEqual(code, ConnectionCode.FromCertificate(second));
        Assert.True(ConnectionCode.Matches(code, first));
        Assert.False(ConnectionCode.Matches(code, second));
    }

    [Fact]
    public void ConnectionCode_AcceptsLooseInputButRejectsWrongLengthOrCharacters()
    {
        using var certificate = ProfessorCertificateStore.CreateEphemeral();
        var code = ConnectionCode.FromCertificate(certificate);
        var loose = " " + code.Replace("-", " ").ToLowerInvariant().Replace('0', 'o').Replace('1', 'l') + " ";

        Assert.True(ConnectionCode.Matches(loose, certificate));
        Assert.False(ConnectionCode.Matches(code[..^1], certificate));
        Assert.False(ConnectionCode.Matches(code + "0", certificate));
        Assert.False(ConnectionCode.TryNormalize("UUUU-UUUU-UUUU", out _)); // U는 문자 집합에 없음
        Assert.False(ConnectionCode.TryNormalize(null, out _));
    }

    [Fact]
    public async Task Framing_RoundTripsAndReturnsNullOnCleanEnd()
    {
        using var stream = new MemoryStream();
        await CollaborationFraming.WriteAsync(stream, new byte[] { 1, 2, 3 });
        stream.Position = 0;

        Assert.Equal(new byte[] { 1, 2, 3 }, await CollaborationFraming.ReadAsync(stream));
        Assert.Null(await CollaborationFraming.ReadAsync(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CollaborationMessageCodec.MaxFrameBytes + 1)]
    public async Task Framing_RejectsBadLengthBeforeReadingBody(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        // 본문 없이 헤더만 있어도 ResourceLimit이 먼저 나와야 한다(본문 버퍼를 할당하지 않음).
        using var stream = new MemoryStream(header);

        var error = await Assert.ThrowsAsync<CollaborationException>(() => CollaborationFraming.ReadAsync(stream));
        Assert.Equal(CollaborationError.ResourceLimit, error.Code);
    }

    [Fact]
    public async Task Framing_TruncatedFrameIsIoError()
    {
        var bytes = new byte[4 + 2];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 10);
        using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAnyAsync<IOException>(() => CollaborationFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task CorrectCode_ConnectsAndExchangesFramesBothWays()
    {
        await using var rig = await Rig.StartAsync();

        await using var student = await rig.ConnectStudentAsync(rig.Listener.ConnectionCode);
        var professor = await rig.Accepted.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        var toProfessor = Channel.CreateUnbounded<byte[]>();
        var toStudent = Channel.CreateUnbounded<byte[]>();
        professor.Start(frame => toProfessor.Writer.WriteAsync(frame).AsTask());
        student.Start(frame => toStudent.Writer.WriteAsync(frame).AsTask());

        await student.SendAsync(Encoding.UTF8.GetBytes("학생→교수자"));
        await professor.SendAsync(Encoding.UTF8.GetBytes("교수자→학생"));

        Assert.Equal("학생→교수자", Encoding.UTF8.GetString(await toProfessor.Reader.ReadAsync().AsTask().WaitAsync(Wait)));
        Assert.Equal("교수자→학생", Encoding.UTF8.GetString(await toStudent.Reader.ReadAsync().AsTask().WaitAsync(Wait)));
    }

    [Fact]
    public async Task ConcurrentSends_ArriveAsWholeFramesInPerSenderOrder()
    {
        await using var rig = await Rig.StartAsync();
        await using var student = await rig.ConnectStudentAsync(rig.Listener.ConnectionCode);
        var professor = await rig.Accepted.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        var received = Channel.CreateUnbounded<byte[]>();
        professor.Start(frame => received.Writer.WriteAsync(frame).AsTask());

        const int PerSender = 50;
        var senders = Enumerable.Range(0, 4).Select(sender => Task.Run(async () =>
        {
            for (var i = 0; i < PerSender; i++)
                await student.SendAsync(Encoding.UTF8.GetBytes($"{sender}:{i}:" + new string('x', 5000)));
        })).ToArray();
        await Task.WhenAll(senders).WaitAsync(Wait);

        var next = new int[4];
        for (var n = 0; n < 4 * PerSender; n++)
        {
            var parts = Encoding.UTF8.GetString(await received.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Split(':');
            var sender = int.Parse(parts[0]);
            Assert.Equal(next[sender]++, int.Parse(parts[1]));
            Assert.Equal(5000, parts[2].Length);
        }
    }

    [Fact]
    public async Task WrongCode_IsRejectedByStudentAndNothingIsAccepted()
    {
        await using var rig = await Rig.StartAsync();
        using var other = ProfessorCertificateStore.CreateEphemeral();

        var error = await Assert.ThrowsAsync<CollaborationException>(
            () => rig.ConnectStudentAsync(ConnectionCode.FromCertificate(other)));

        Assert.Equal(CollaborationError.NotAuthorized, error.Code);
        await Task.Delay(200);
        Assert.False(rig.Accepted.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD")]
    [InlineData("not-a-code-at-all")]
    public async Task MalformedCode_FailsBeforeConnecting(string code)
    {
        await using var rig = await Rig.StartAsync();

        var error = await Assert.ThrowsAsync<CollaborationException>(() => rig.ConnectStudentAsync(code));

        Assert.Equal(CollaborationError.InvalidRequest, error.Code);
        Assert.DoesNotContain(rig.Log.Snapshot(), line => line.Contains("연결 거부"));
    }

    [Fact]
    public async Task PlaintextClient_IsDroppedAndListenerKeepsWorking()
    {
        await using var rig = await Rig.StartAsync();

        using (var plain = new TcpClient())
        {
            await plain.ConnectAsync("127.0.0.1", rig.Listener.Port);
            var stream = plain.GetStream();
            var frame = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(frame, 4);
            await stream.WriteAsync(frame);
            // 서버가 TLS 협상 실패로 닫아야 한다.
            var buffer = new byte[1024];
            int read;
            do { read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Wait); } while (read > 0);
        }

        await using var student = await rig.ConnectStudentAsync(rig.Listener.ConnectionCode);
        Assert.NotNull(await rig.Accepted.Reader.ReadAsync().AsTask().WaitAsync(Wait));
    }

    [Fact]
    public async Task SilentClient_IsClosedAfterHandshakeTimeout()
    {
        await using var rig = await Rig.StartAsync(handshakeTimeout: TimeSpan.FromMilliseconds(300));
        using var silent = new TcpClient();
        await silent.ConnectAsync("127.0.0.1", rig.Listener.Port);

        var read = await silent.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(Wait);

        Assert.Equal(0, read);
        Assert.False(rig.Accepted.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("{\"Capability\":\"edustream.collaboration/2\",\"Version\":1,\"Role\":1}")]
    [InlineData("{\"Capability\":\"edustream.collaboration/1\",\"Version\":2,\"Role\":1}")]
    [InlineData("{\"Capability\":\"edustream.collaboration/1\",\"Version\":1,\"Role\":0}")]
    [InlineData("not json")]
    public async Task WrongHello_IsRejectedAfterTls(string hello)
    {
        await using var rig = await Rig.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", rig.Listener.Port);
        // 테스트 전용: 서버 거부 경로만 보려고 인증서 확인을 생략한다. 제품 코드는 접속 코드 확인을 생략하지 않는다.
        await using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "test",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        });

        await CollaborationFraming.WriteAsync(ssl, Encoding.UTF8.GetBytes(hello));

        Assert.Null(await ReadOrEndAsync(ssl));
        Assert.False(rig.Accepted.Reader.TryRead(out _));
    }

    [Fact]
    public async Task OversizedHello_IsRejected()
    {
        await using var rig = await Rig.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", rig.Listener.Port);
        await using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "test" });

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, CollaborationHandshake.MaxHelloBytes + 1);
        await ssl.WriteAsync(header);
        await ssl.FlushAsync();

        Assert.Null(await ReadOrEndAsync(ssl));
        Assert.Contains(rig.Log.Snapshot(), line => line.Contains("capability 거부(ResourceLimit)"));
    }

    [Fact]
    public async Task ListenerDispose_ClosesAcceptedConnections()
    {
        var rig = await Rig.StartAsync();
        await using var student = await rig.ConnectStudentAsync(rig.Listener.ConnectionCode);
        var professor = await rig.Accepted.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        professor.Start(_ => Task.CompletedTask);
        student.Start(_ => Task.CompletedTask);

        await rig.DisposeAsync();

        await professor.Completion.WaitAsync(Wait);
        await student.Completion.WaitAsync(Wait);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => student.SendAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task HandlerException_DoesNotCloseConnection()
    {
        await using var rig = await Rig.StartAsync();
        await using var student = await rig.ConnectStudentAsync(rig.Listener.ConnectionCode);
        var professor = await rig.Accepted.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        var second = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        professor.Start(frame =>
        {
            if (Interlocked.Increment(ref count) == 1) throw new InvalidOperationException("처리기 오류(대역)");
            second.TrySetResult(frame);
            return Task.CompletedTask;
        });

        await student.SendAsync(new byte[] { 1 });
        await student.SendAsync(new byte[] { 2 });

        Assert.Equal(new byte[] { 2 }, await second.Task.WaitAsync(Wait));
        Assert.False(professor.IsClosed);
    }

    [Fact]
    public void CollaborationPort_IsSessionPortPlusOne()
    {
        Assert.Equal(5001, CollaborationPorts.ForSession(5000));
        Assert.Throws<ArgumentOutOfRangeException>(() => CollaborationPorts.ForSession(65535));
        Assert.Throws<ArgumentOutOfRangeException>(() => CollaborationPorts.ForSession(0));
    }

    private static async Task<byte[]?> ReadOrEndAsync(Stream stream)
    {
        try
        {
            return await CollaborationFraming.ReadAsync(stream).WaitAsync(Wait);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private int _disposed;

        public System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate { get; } =
            ProfessorCertificateStore.CreateEphemeral();
        public InMemoryLogSink Log { get; } = new();
        public SecureCollaborationListener Listener { get; }
        public Channel<SecureCollaborationConnection> Accepted { get; } = Channel.CreateUnbounded<SecureCollaborationConnection>();

        private Rig(TimeSpan? handshakeTimeout)
        {
            Listener = new SecureCollaborationListener(Certificate, Log, handshakeTimeout);
            Listener.ConnectionAccepted += connection => Accepted.Writer.WriteAsync(connection).AsTask();
        }

        public static Task<Rig> StartAsync(TimeSpan? handshakeTimeout = null)
        {
            var rig = new Rig(handshakeTimeout);
            rig.Listener.Start(TestPortAllocator.GetFreePortPair());
            return Task.FromResult(rig);
        }

        public Task<SecureCollaborationConnection> ConnectStudentAsync(string code) =>
            SecureCollaborationConnector.ConnectAsync("127.0.0.1", Listener.Port, code, new InMemoryLogSink(), Wait);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await Listener.DisposeAsync();
            Certificate.Dispose();
        }
    }
}
