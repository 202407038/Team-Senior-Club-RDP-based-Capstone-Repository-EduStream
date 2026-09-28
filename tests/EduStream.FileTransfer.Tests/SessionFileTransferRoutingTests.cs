using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;
using EduStream.Core.Logging;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당: 파일 목록 전달·다운로드 요청 라우팅·청크·취소·저장 완료를 교수자 라우터와 학생 클라이언트 사이에서 검증합니다.
/// 보호 채널이 확정되지 않아 메모리 채널로 연결한 대역 기준 검증이며, 실제 네트워크 전송은 포함하지 않습니다.
/// </summary>
public sealed class SessionFileTransferRoutingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Attach_SendsCurrentCatalogToStudent()
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(1024);

        await using var peer = await fixture.ConnectAsync();

        Assert.Equal(file, Assert.Single(peer.Client.Catalog!.Files));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    [InlineData(FileTransferRules.DefaultChunkSize * 3 + 17)]
    public async Task Download_SavesIdenticalFileAndServerSeesStoredNotice(int length)
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(length);
        await using var peer = await fixture.ConnectAsync();
        var stored = new TaskCompletionSource<FileStoredNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Router.FileStored += (connection, notice) =>
        {
            if (connection == fixture.Student) stored.TrySetResult(notice);
        };

        var receipt = await peer.Client.DownloadAsync(file.FileId).WaitAsync(Timeout);

        Assert.Equal(await File.ReadAllBytesAsync(fixture.SourcePath(file)), await File.ReadAllBytesAsync(receipt.LocalPath));
        var notice = await stored.Task.WaitAsync(Timeout);
        Assert.Equal(file.FileId, notice.FileId);
        Assert.Equal(file.Sha256, notice.Sha256, ignoreCase: true);
        await WaitUntilAsync(() => fixture.Router.PendingTransferCount == 0);
        Assert.Equal(0, peer.Client.PendingDownloadCount);
    }

    [Fact]
    public async Task RegisterAndUnregisterThroughSessionManager_PublishesCatalogToAttachedStudent()
    {
        await using var rig = await SessionRig.OpenAsync();
        var student = rig.SessionManager.Participants.Join("client-a", rig.SessionId, "Alice", ParticipantRole.Student);
        var toStudent = new RecordingChannel();
        await rig.SessionManager.FileTransfers!.AttachAsync(student, toStudent);
        Assert.Empty((await toStudent.NextAsync<SessionFileCatalogSnapshot>()).Files);

        var descriptor = await rig.SessionManager.RegisterFileAsync(await rig.WriteSourceAsync(256));
        var afterRegister = await toStudent.NextAsync<SessionFileCatalogSnapshot>();
        Assert.Equal(descriptor.FileId, Assert.Single(afterRegister.Files).FileId);

        Assert.True(rig.SessionManager.UnregisterFile(descriptor.FileId));
        var afterUnregister = await toStudent.NextAsync<SessionFileCatalogSnapshot>();
        Assert.Empty(afterUnregister.Files);
        Assert.True(afterUnregister.Revision > afterRegister.Revision);
    }

    [Fact]
    public async Task CloseSession_DisposesRouter()
    {
        await using var rig = await SessionRig.OpenAsync();
        Assert.NotNull(rig.SessionManager.FileTransfers);

        await rig.CloseAsync();

        Assert.Null(rig.SessionManager.FileTransfers);
    }

    [Fact]
    public async Task StudentCancel_StopsServerTransferAndLeavesNoFile()
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(FileTransferRules.DefaultChunkSize * 4);
        await using var peer = await fixture.ConnectAsync();
        var gate = peer.ToClient.HoldChunksAfter(0);
        using var cancellation = new CancellationTokenSource();

        var download = peer.Client.DownloadAsync(file.FileId, cancellationToken: cancellation.Token);
        await gate.Reached.Task.WaitAsync(Timeout);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(Timeout));
        await WaitUntilAsync(() => fixture.Router.PendingTransferCount == 0);
        Assert.Empty(fixture.DownloadedFiles());
    }

    [Fact]
    public async Task UnregisterDuringTransfer_CatalogUpdateCancelsStudentDownload()
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(FileTransferRules.DefaultChunkSize * 4);
        await using var peer = await fixture.ConnectAsync();
        var gate = peer.ToClient.HoldChunksAfter(0);

        var download = peer.Client.DownloadAsync(file.FileId);
        await gate.Reached.Task.WaitAsync(Timeout);
        Assert.True(fixture.Catalog.Unregister(file.FileId));
        await fixture.Router.PublishCatalogAsync();

        var error = await Assert.ThrowsAsync<CollaborationException>(() => download.WaitAsync(Timeout));
        Assert.Equal(CollaborationError.FileUnavailable, error.Code);
        // 학생이 보낸 취소로 교수자 쪽 대기 중 송신도 끝나야 한다.
        await WaitUntilAsync(() => fixture.Router.PendingTransferCount == 0);
        Assert.Empty(fixture.DownloadedFiles());
    }

    [Fact]
    public async Task UnregisterDuringTransfer_ServerStopsNextChunkWithFailureNotice()
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(FileTransferRules.DefaultChunkSize * 4);
        await using var peer = await fixture.ConnectAsync();
        var gate = peer.ToClient.HoldChunksAfter(0);

        var download = peer.Client.DownloadAsync(file.FileId);
        await gate.Reached.Task.WaitAsync(Timeout);
        // 목록 갱신 없이 등록만 해제: 교수자 카탈로그가 다음 청크 전에 끊고 실패 알림을 보내야 한다.
        Assert.True(fixture.Catalog.Unregister(file.FileId));
        gate.Release.TrySetResult();

        var error = await Assert.ThrowsAsync<CollaborationException>(() => download.WaitAsync(Timeout));
        Assert.Equal(CollaborationError.FileUnavailable, error.Code);
        await WaitUntilAsync(() => fixture.Router.PendingTransferCount == 0);
        Assert.Empty(fixture.DownloadedFiles());
    }

    [Fact]
    public async Task StudentDisconnected_CancelsServerTransferAndFailsDownload()
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(FileTransferRules.DefaultChunkSize * 4);
        await using var peer = await fixture.ConnectAsync();
        var gate = peer.ToClient.HoldChunksAfter(0);

        var download = peer.Client.DownloadAsync(file.FileId);
        await gate.Reached.Task.WaitAsync(Timeout);
        fixture.Registry.Disconnect(Fixture.StudentClientId);
        peer.Client.ConnectionClosed();

        var error = await Assert.ThrowsAsync<CollaborationException>(() => download.WaitAsync(Timeout));
        Assert.Equal(CollaborationError.TransferIncomplete, error.Code);
        await WaitUntilAsync(() => fixture.Router.PendingTransferCount == 0);
        Assert.Empty(fixture.DownloadedFiles());
        await Assert.ThrowsAsync<CollaborationException>(() => peer.Client.DownloadAsync(file.FileId));
    }

    [Fact]
    public async Task Attach_RejectsStaleProfessorAndReplacedConnections()
    {
        using var fixture = new Fixture();
        var channel = new RecordingChannel();

        var unknown = new ParticipantConnection(fixture.SessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
        var professor = fixture.Student with { Role = ParticipantRole.Professor };
        var old = fixture.Student;
        fixture.Registry.Join(Fixture.StudentClientId, fixture.SessionId, "Alice", ParticipantRole.Student); // 재접속으로 교체

        foreach (var connection in new[] { unknown, professor, old })
        {
            var error = await Assert.ThrowsAsync<CollaborationException>(() => fixture.Router.AttachAsync(connection, channel));
            Assert.Equal(CollaborationError.NotAuthorized, error.Code);
        }
        Assert.Equal(0, channel.Count);
    }

    [Fact]
    public async Task FramesFromUnattachedOrMismatchedConnection_AreIgnored()
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(64);
        var channel = new RecordingChannel();
        await fixture.Router.AttachAsync(fixture.Student, channel);
        await channel.NextAsync<SessionFileCatalogSnapshot>();
        var sentBefore = channel.Count;
        var request = Encode(new SessionFileRequest(Guid.NewGuid(), fixture.SessionId, file.FileId, file.Revision));

        var stranger = new ParticipantConnection(fixture.SessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student);
        await fixture.Router.HandleFrameAsync(stranger, request);
        await fixture.Router.HandleFrameAsync(fixture.Student with { ParticipantId = Guid.NewGuid() }, request);
        // 학생이 교수자 전용 메시지(목록)를 보내도 처리하지 않는다.
        await fixture.Router.HandleFrameAsync(fixture.Student, Encode(fixture.Catalog.GetSnapshot()));

        Assert.Equal(0, fixture.Router.PendingTransferCount);
        Assert.Equal(sentBefore, channel.Count);
    }

    [Fact]
    public async Task StoredNotice_OnlyAcceptedAfterAllChunksAndWhenItMatches()
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(1024);
        var channel = new RecordingChannel();
        await fixture.Router.AttachAsync(fixture.Student, channel);
        await channel.NextAsync<SessionFileCatalogSnapshot>();
        var raised = 0;
        fixture.Router.FileStored += (_, _) => Interlocked.Increment(ref raised);

        var request = new SessionFileRequest(Guid.NewGuid(), fixture.SessionId, file.FileId, file.Revision);
        await fixture.Router.HandleFrameAsync(fixture.Student, Encode(request));
        await channel.NextAsync<SessionFileChunk>();
        await WaitUntilAsync(() => fixture.Log.Snapshot().Any(line =>
            line.Contains("저장 확인 대기") && line.Contains(request.RequestId.ToString())));

        await fixture.Router.HandleFrameAsync(fixture.Student,
            Encode(new FileStoredNotice(request.RequestId, file.FileId, file.Length, new string('0', 64))));

        Assert.Equal(0, raised);
        // 불일치 알림으로 요청은 정리되고, 같은 요청의 뒤늦은 올바른 알림도 받지 않는다.
        Assert.Equal(0, fixture.Router.PendingTransferCount);
        await fixture.Router.HandleFrameAsync(fixture.Student,
            Encode(new FileStoredNotice(request.RequestId, file.FileId, file.Length, file.Sha256)));
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task RequestForUnknownFileOrDuplicateId_GetsFailureNotice()
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(FileTransferRules.DefaultChunkSize * 2);
        var channel = new RecordingChannel { HoldChunks = true };
        await fixture.Router.AttachAsync(fixture.Student, channel);
        await channel.NextAsync<SessionFileCatalogSnapshot>();

        var unknown = new SessionFileRequest(Guid.NewGuid(), fixture.SessionId, Guid.NewGuid(), 1);
        await fixture.Router.HandleFrameAsync(fixture.Student, Encode(unknown));
        var unknownFailure = await channel.NextAsync<CollaborationFailureNotice>();
        Assert.Equal((unknown.RequestId, CollaborationError.FileUnavailable), (unknownFailure.RequestId, unknownFailure.Error));

        var request = new SessionFileRequest(Guid.NewGuid(), fixture.SessionId, file.FileId, file.Revision);
        await fixture.Router.HandleFrameAsync(fixture.Student, Encode(request));
        await fixture.Router.HandleFrameAsync(fixture.Student, Encode(request));
        var duplicate = await channel.NextAsync<CollaborationFailureNotice>();
        Assert.Equal((request.RequestId, CollaborationError.InvalidRequest), (duplicate.RequestId, duplicate.Error));
        Assert.Equal(1, fixture.Router.PendingTransferCount);
    }

    [Fact]
    public async Task RequestsBeyondPerConnectionLimit_GetResourceLimit()
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(FileTransferRules.DefaultChunkSize * 2);
        var channel = new RecordingChannel { HoldChunks = true };
        await fixture.Router.AttachAsync(fixture.Student, channel);
        await channel.NextAsync<SessionFileCatalogSnapshot>();

        for (var i = 0; i < SessionFileLimits.MaxConcurrentTransfers; i++)
            await fixture.Router.HandleFrameAsync(fixture.Student,
                Encode(new SessionFileRequest(Guid.NewGuid(), fixture.SessionId, file.FileId, file.Revision)));
        var extra = new SessionFileRequest(Guid.NewGuid(), fixture.SessionId, file.FileId, file.Revision);
        await fixture.Router.HandleFrameAsync(fixture.Student, Encode(extra));

        var failure = await channel.NextAsync<CollaborationFailureNotice>();
        Assert.Equal((extra.RequestId, CollaborationError.ResourceLimit), (failure.RequestId, failure.Error));
        Assert.Equal(SessionFileLimits.MaxConcurrentTransfers, fixture.Router.PendingTransferCount);
    }

    [Fact]
    public async Task Client_IgnoresOlderOrOtherSessionCatalog()
    {
        using var fixture = new Fixture();
        await fixture.RegisterAsync(64);
        await using var peer = await fixture.ConnectAsync();
        var current = peer.Client.Catalog!;

        await peer.Client.HandleFrameAsync(Encode(current with { Revision = current.Revision - 1, Files = [] }));
        var otherSession = Guid.NewGuid();
        await peer.Client.HandleFrameAsync(Encode(new SessionFileCatalogSnapshot(otherSession, current.Revision + 5, [])));

        Assert.Same(current, peer.Client.Catalog);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"Kind\":999}")]
    [InlineData("{\"Kind\":\"FileRequest\"}")]
    public void PeekKind_RejectsMalformedEnvelope(string json)
    {
        var error = Assert.Throws<CollaborationException>(() =>
            CollaborationFrameInspector.PeekKind(System.Text.Encoding.UTF8.GetBytes(json)));
        Assert.Contains(error.Code, new[] { CollaborationError.InvalidRequest, CollaborationError.ResourceLimit });
    }

    [Fact]
    public void PeekKind_RejectsOversizedFrameBeforeParsing()
    {
        var error = Assert.Throws<CollaborationException>(() =>
            CollaborationFrameInspector.PeekKind(new byte[CollaborationMessageCodec.MaxFrameBytes + 1]));
        Assert.Equal(CollaborationError.ResourceLimit, error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    [InlineData(FileTransferRules.DefaultChunkSize * 5 + 17)]
    public async Task StoredBeforeFinalSendReturns_IsAcceptedOnceAfterSendSucceeds(int length)
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(length);
        SessionFileRequestClient? client = null;
        var earlyAck = new TaskCompletionSource<FileStoredNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var raised = 0;
        fixture.Router.FileStored += (_, _) => { Interlocked.Increment(ref raised); stored.TrySetResult(); };
        var toClient = new CallbackChannel(async (frame, token) =>
        {
            await client!.HandleFrameAsync(frame, token);
            if (CollaborationFrameInspector.PeekKind(frame) == CollaborationMessageKind.FileChunk &&
                CollaborationMessageCodec.Decode<SessionFileChunk>(frame, out _).Index == file.TotalChunks - 1)
                await releaseSend.Task.WaitAsync(token);
        });
        var toServer = new CallbackChannel(async (frame, token) =>
        {
            await fixture.Router.HandleFrameAsync(fixture.Student, frame);
            if (CollaborationFrameInspector.PeekKind(frame) == CollaborationMessageKind.FileStored)
                earlyAck.TrySetResult(CollaborationMessageCodec.Decode<FileStoredNotice>(frame, out _));
        });
        client = new SessionFileRequestClient(fixture.SessionId, toServer,
            new SessionFileDownloader(new ReadinessDirectory(fixture.Downloads)), fixture.Log);
        await fixture.Router.AttachAsync(fixture.Student, toClient);
        try
        {
            var receipt = await client.DownloadAsync(file.FileId).WaitAsync(Timeout);
            var notice = await earlyAck.Task.WaitAsync(Timeout);
            Assert.Equal(await File.ReadAllBytesAsync(fixture.SourcePath(file)), await File.ReadAllBytesAsync(receipt.LocalPath));
            Assert.Equal(0, Volatile.Read(ref raised));
            Assert.Equal(1, fixture.Router.PendingTransferCount);
            // 같은 ACK를 여러 번 받아도 송신 성공 이후 딱 한 번만 완료한다.
            await fixture.Router.HandleFrameAsync(fixture.Student, Encode(notice));
            releaseSend.TrySetResult();
            await stored.Task.WaitAsync(Timeout);
            await fixture.Router.HandleFrameAsync(fixture.Student, Encode(notice));
            Assert.Equal(1, Volatile.Read(ref raised));
            Assert.Equal(0, fixture.Router.PendingTransferCount);
        }
        finally { releaseSend.TrySetResult(); }
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("disconnect")]
    public async Task EarlyStored_DoesNotCompleteAfterSendFailureCancelOrDisconnect(string end)
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(1024);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new CallbackChannel(async (frame, token) =>
        {
            if (CollaborationFrameInspector.PeekKind(frame) != CollaborationMessageKind.FileChunk) return;
            reached.TrySetResult();
            await release.Task.WaitAsync(token);
            if (end == "failure") throw new IOException("송신 실패 대역");
        });
        await fixture.Router.AttachAsync(fixture.Student, channel);
        var raised = 0;
        fixture.Router.FileStored += (_, _) => Interlocked.Increment(ref raised);
        var request = new SessionFileRequest(Guid.NewGuid(), fixture.SessionId, file.FileId, file.Revision);
        await fixture.Router.HandleFrameAsync(fixture.Student, Encode(request));
        try
        {
            await reached.Task.WaitAsync(Timeout);
            var notice = new FileStoredNotice(request.RequestId, file.FileId, file.Length, file.Sha256);
            await fixture.Router.HandleFrameAsync(fixture.Student, Encode(notice));
            Assert.Equal(0, Volatile.Read(ref raised));
            if (end == "cancel")
                await fixture.Router.HandleFrameAsync(fixture.Student, Encode(new FileCancelRequest(request.RequestId, fixture.SessionId)));
            if (end == "disconnect") fixture.Registry.Disconnect(Fixture.StudentClientId);
            release.TrySetResult();
            await WaitUntilAsync(() => fixture.Router.PendingTransferCount == 0);
            await fixture.Router.HandleFrameAsync(fixture.Student, Encode(notice));
            Assert.Equal(0, Volatile.Read(ref raised));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task StoredBeforeFinalChunkOrWithWrongMetadata_IsNotBufferedAsSuccess()
    {
        using var fixture = new Fixture();
        var file = await fixture.RegisterAsync(FileTransferRules.DefaultChunkSize + 1);
        var firstReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new CallbackChannel(async (frame, token) =>
        {
            if (CollaborationFrameInspector.PeekKind(frame) != CollaborationMessageKind.FileChunk) return;
            var chunk = CollaborationMessageCodec.Decode<SessionFileChunk>(frame, out _);
            if (chunk.Index == 0) { firstReached.TrySetResult(); await releaseFirst.Task.WaitAsync(token); }
            else { lastReached.TrySetResult(); await releaseLast.Task.WaitAsync(token); }
        });
        await fixture.Router.AttachAsync(fixture.Student, channel);
        var raised = 0;
        fixture.Router.FileStored += (_, _) => Interlocked.Increment(ref raised);
        var request = new SessionFileRequest(Guid.NewGuid(), fixture.SessionId, file.FileId, file.Revision);
        var notice = new FileStoredNotice(request.RequestId, file.FileId, file.Length, file.Sha256);
        await fixture.Router.HandleFrameAsync(fixture.Student, Encode(request));
        try
        {
            await firstReached.Task.WaitAsync(Timeout);
            await fixture.Router.HandleFrameAsync(fixture.Student, Encode(notice));
            releaseFirst.TrySetResult();
            await lastReached.Task.WaitAsync(Timeout);
            await fixture.Router.HandleFrameAsync(fixture.Student, Encode(notice with { Sha256 = new string('0', 64) }));
            releaseLast.TrySetResult();
            await WaitUntilAsync(() => fixture.Log.Snapshot().Any(line => line.Contains("저장 확인 대기")));
            Assert.Equal(0, Volatile.Read(ref raised));
            Assert.Equal(1, fixture.Router.PendingTransferCount);
            await fixture.Router.HandleFrameAsync(fixture.Student, Encode(notice));
            Assert.Equal(1, Volatile.Read(ref raised));
            Assert.Equal(0, fixture.Router.PendingTransferCount);
        }
        finally { releaseFirst.TrySetResult(); releaseLast.TrySetResult(); }
    }

    private sealed class CallbackChannel(Func<byte[], CancellationToken, Task> send) : ICollaborationChannel
    {
        public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default) => send(frame, cancellationToken);
    }

    private static byte[] Encode<T>(T payload) where T : notnull => CollaborationMessageCodec.Encode(Guid.NewGuid(), payload);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건이 제한 시간 안에 충족되지 않았습니다.");
            await Task.Delay(10);
        }
    }

    /// <summary>교수자 레지스트리·카탈로그·라우터를 네트워크 없이 조립한 대역입니다.</summary>
    private sealed class Fixture : IDisposable
    {
        public const string StudentClientId = "client-a";
        private readonly Dictionary<Guid, string> _sources = new();

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "EduStream-file-route-" + Guid.NewGuid().ToString("N"));
        public string Downloads => Path.Combine(Root, "downloads");
        public Guid SessionId { get; } = Guid.NewGuid();
        public ParticipantRegistry Registry { get; } = new();
        public SessionFileCatalog Catalog { get; }
        public SessionFileTransferRouter Router { get; }
        public ParticipantConnection Student { get; }
        public InMemoryLogSink Log { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Catalog = new SessionFileCatalog(SessionId, new SessionFileRequestAuthorizer(Registry));
            Router = new SessionFileTransferRouter(Catalog, Registry, Log);
            Student = Registry.Join(StudentClientId, SessionId, "Alice", ParticipantRole.Student);
        }

        public async Task<SessionFileDescriptor> RegisterAsync(int length)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".bin");
            var content = new byte[length];
            new Random(length).NextBytes(content);
            await File.WriteAllBytesAsync(path, content);
            var file = await Catalog.RegisterAsync(path);
            _sources[file.FileId] = path;
            return file;
        }

        public string SourcePath(SessionFileDescriptor file) => _sources[file.FileId];

        public string[] DownloadedFiles() =>
            Directory.Exists(Downloads) ? Directory.GetFiles(Downloads) : [];

        public async Task<Peer> ConnectAsync()
        {
            SessionFileRequestClient? client = null;
            var toClient = new PumpChannel(frame => client!.HandleFrameAsync(frame));
            var toServer = new PumpChannel(frame => Router.HandleFrameAsync(Student, frame));
            client = new SessionFileRequestClient(SessionId, toServer,
                new SessionFileDownloader(new ReadinessDirectory(Downloads)), Log);
            await Router.AttachAsync(Student, toClient);
            await WaitUntilAsync(() => client.Catalog is not null);
            return new Peer(client, toClient, toServer);
        }

        public void Dispose()
        {
            Router.Dispose();
            Catalog.Dispose();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    private sealed record Peer(SessionFileRequestClient Client, PumpChannel ToClient, PumpChannel ToServer) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await ToClient.DisposeAsync();
            await ToServer.DisposeAsync();
        }
    }

    private sealed class ChunkGate
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>보낸 순서대로 상대 처리기에 넘기는 메모리 채널. 필요하면 특정 청크 이후 송신자를 붙잡습니다.</summary>
    private sealed class PumpChannel : ICollaborationChannel, IAsyncDisposable
    {
        private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>();
        private readonly Task _pump;
        private (int AfterIndex, ChunkGate Gate)? _hold;

        public PumpChannel(Func<byte[], Task> deliver)
        {
            _pump = Task.Run(async () =>
            {
                await foreach (var frame in _queue.Reader.ReadAllAsync())
                {
                    try { await deliver(frame); } catch { }
                }
            });
        }

        public ChunkGate HoldChunksAfter(int index)
        {
            var gate = new ChunkGate();
            _hold = (index, gate);
            return gate;
        }

        public async Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
        {
            if (_hold is { } hold && CollaborationFrameInspector.PeekKind(frame) == CollaborationMessageKind.FileChunk &&
                CollaborationMessageCodec.Decode<SessionFileChunk>(frame, out _).Index > hold.AfterIndex)
            {
                hold.Gate.Reached.TrySetResult();
                await hold.Gate.Release.Task.WaitAsync(cancellationToken);
            }
            await _queue.Writer.WriteAsync(frame, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _hold?.Gate.Release.TrySetResult();
            _queue.Writer.TryComplete();
            await _pump.WaitAsync(Timeout);
        }
    }

    /// <summary>교수자가 보낸 프레임을 기록합니다. HoldChunks면 청크 송신을 끝내지 않아 전송이 진행 중으로 남습니다.</summary>
    private sealed class RecordingChannel : ICollaborationChannel
    {
        private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>();
        private int _count;

        public bool HoldChunks { get; init; }
        public int Count => Volatile.Read(ref _count);

        public async Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
        {
            if (HoldChunks && CollaborationFrameInspector.PeekKind(frame) == CollaborationMessageKind.FileChunk)
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            }
            Interlocked.Increment(ref _count);
            await _frames.Writer.WriteAsync(frame, cancellationToken);
        }

        public async Task<T> NextAsync<T>() where T : notnull
        {
            var frame = await _frames.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            return CollaborationMessageCodec.Decode<T>(frame, out _);
        }
    }

    private sealed class SessionRig : IAsyncDisposable
    {
        private bool _closed;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "EduStream-file-route-session-" + Guid.NewGuid().ToString("N"));
        public SessionManager SessionManager { get; }
        public Guid SessionId => SessionManager.CurrentSession!.SessionId;

        private SessionRig(SessionManager sessionManager)
        {
            SessionManager = sessionManager;
            Directory.CreateDirectory(Root);
        }

        public static async Task<SessionRig> OpenAsync()
        {
            var log = new InMemoryLogSink();
            var sessionManager = new SessionManager(log, new TcpServerService(log, new PacketSerializer()));
            await sessionManager.OpenSessionAsync("FileRouteTest", GetFreePort());
            return new SessionRig(sessionManager);
        }

        public async Task<string> WriteSourceAsync(int length)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".bin");
            await File.WriteAllBytesAsync(path, new byte[length]);
            return path;
        }

        public async Task CloseAsync()
        {
            _closed = true;
            await SessionManager.CloseSessionAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (!_closed) await SessionManager.CloseSessionAsync();
            try { Directory.Delete(Root, true); } catch { }
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
