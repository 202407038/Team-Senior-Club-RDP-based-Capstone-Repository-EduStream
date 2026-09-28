using System.Threading.Channels;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;
using EduStream.Core.Logging;

namespace EduStream.Client.Services;

/// <summary>
/// 2번 구현: 학생 쪽 파일 목록 수신과 다운로드 요청/청크 분리/취소/저장 완료 알림을 담당합니다.
/// 받은 청크는 요청 ID별 작은 버퍼로 나눠 4번 저장기(<see cref="ISessionFileDownloader"/>)에 순서대로 넘깁니다.
/// </summary>
/// <remarks>
/// 현재 단계에서는 보호 채널 계약이 없어 이 클래스를 ClientViewModel에 연결하지 않았습니다.
/// 채널이 확정되면 수신 프레임을 <see cref="HandleFrameAsync"/>로, 연결 종료를 <see cref="ConnectionClosed"/>로 넘깁니다.
/// </remarks>
public sealed class SessionFileRequestClient
{
    // 수신 루프가 디스크 저장보다 앞서 달려 메모리에 청크를 쌓지 않게 하는 요청별 상한.
    private const int ChunkBufferCapacity = 4;

    private sealed class PendingDownload(SessionFileDescriptor file)
    {
        public SessionFileDescriptor File { get; } = file;
        public Channel<SessionFileChunk> Chunks { get; } = Channel.CreateBounded<SessionFileChunk>(
            new BoundedChannelOptions(ChunkBufferCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        // 교수자 실패 알림으로 끝난 요청은 취소를 되돌려 보낼 필요가 없다.
        public bool EndedByServer { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, PendingDownload> _pending = new(); // requestId → 진행 중 다운로드
    private readonly ICollaborationChannel _server;
    private readonly ISessionFileDownloader _downloader;
    private readonly ILogSink _logSink;
    private readonly Guid _sessionId;
    private SessionFileCatalogSnapshot? _catalog;
    private bool _closed;

    /// <summary>교수자가 보낸 더 새로운 파일 목록을 적용한 뒤 발생합니다.</summary>
    public event Action<SessionFileCatalogSnapshot>? CatalogChanged;

    public SessionFileRequestClient(Guid sessionId, ICollaborationChannel server,
        ISessionFileDownloader downloader, ILogSink logSink)
    {
        CollaborationContract.RequireId(sessionId, nameof(sessionId));
        _sessionId = sessionId;
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
    }

    public SessionFileCatalogSnapshot? Catalog
    {
        get { lock (_gate) return _catalog; }
    }

    public int PendingDownloadCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>
    /// 교수자 연결에서 받은 프레임을 처리합니다. 청크 버퍼가 가득 차면 저장이 따라올 때까지 기다리므로
    /// 수신 루프에서 순서대로 await해야 합니다.
    /// </summary>
    public async Task HandleFrameAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        try
        {
            switch (CollaborationFrameInspector.PeekKind(frame))
            {
                case CollaborationMessageKind.FileCatalog:
                    ApplyCatalog(CollaborationMessageCodec.Decode<SessionFileCatalogSnapshot>(frame, out _));
                    break;
                case CollaborationMessageKind.FileChunk:
                    await RouteChunkAsync(CollaborationMessageCodec.Decode<SessionFileChunk>(frame, out _), cancellationToken);
                    break;
                case CollaborationMessageKind.Failure:
                    ApplyFailure(CollaborationMessageCodec.Decode<CollaborationFailureNotice>(frame, out _));
                    break;
                default:
                    throw new CollaborationException(CollaborationError.InvalidRequest);
            }
        }
        catch (CollaborationException ex)
        {
            _logSink.Write($"[FileRoute] 잘못된 프레임 무시: 사유={ex.Code}");
        }
    }

    /// <summary>
    /// 현재 목록의 파일 하나를 새 요청 ID로 받아 다운로드 폴더에 저장합니다.
    /// 저장까지 끝난 뒤에만 교수자에게 저장 완료를 알리며, 실패·취소 시에는 교수자 송신을 취소시킵니다.
    /// </summary>
    public async Task<DownloadReceipt> DownloadAsync(Guid fileId, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SessionFileRequest request;
        PendingDownload pending;
        lock (_gate)
        {
            if (_closed) throw new CollaborationException(CollaborationError.SessionClosed);
            var file = _catalog?.Files.FirstOrDefault(candidate => candidate.FileId == fileId)
                ?? throw new CollaborationException(CollaborationError.FileUnavailable);
            request = new SessionFileRequest(Guid.NewGuid(), _sessionId, file.FileId, file.Revision);
            pending = new PendingDownload(file);
            _pending.Add(request.RequestId, pending);
        }

        var completed = false;
        try
        {
            await _server.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), request), cancellationToken);
            var receipt = await _downloader.SaveAsync(pending.File, request,
                pending.Chunks.Reader.ReadAllAsync(cancellationToken), progress, cancellationToken);
            completed = true;
            await SendBestEffortAsync(new FileStoredNotice(request.RequestId, receipt.FileId, receipt.Length, receipt.Sha256));
            _logSink.Write($"[FileRoute] 저장 완료: request={request.RequestId}");
            return receipt;
        }
        finally
        {
            bool notifyCancel;
            lock (_gate)
            {
                _pending.Remove(request.RequestId);
                notifyCancel = !completed && !pending.EndedByServer && !_closed;
            }
            pending.Chunks.Writer.TryComplete();
            if (notifyCancel)
                await SendBestEffortAsync(new FileCancelRequest(request.RequestId, _sessionId));
        }
    }

    /// <summary>교수자 연결이 끊기면 호출합니다. 진행 중인 다운로드는 임시 파일을 지우고 실패합니다.</summary>
    public void ConnectionClosed()
    {
        PendingDownload[] pending;
        lock (_gate)
        {
            _closed = true;
            pending = _pending.Values.ToArray();
        }
        foreach (var download in pending)
            download.Chunks.Writer.TryComplete(new CollaborationException(CollaborationError.TransferIncomplete));
    }

    private void ApplyCatalog(SessionFileCatalogSnapshot catalog)
    {
        PendingDownload[] removed;
        lock (_gate)
        {
            if (catalog.SessionId != _sessionId || (_catalog is not null && catalog.Revision <= _catalog.Revision))
                return;
            _catalog = catalog;
            // 등록 해제된 파일의 진행 중 요청은 교수자 실패 알림을 기다리지 않고 바로 끊는다.
            removed = _pending.Values
                .Where(download => !catalog.Files.Any(file =>
                    file.FileId == download.File.FileId && file.Revision == download.File.Revision))
                .ToArray();
        }
        foreach (var download in removed)
            download.Chunks.Writer.TryComplete(new CollaborationException(CollaborationError.FileUnavailable));
        CatalogChanged?.Invoke(catalog);
    }

    private async Task RouteChunkAsync(SessionFileChunk chunk, CancellationToken cancellationToken)
    {
        PendingDownload? pending;
        lock (_gate) _pending.TryGetValue(chunk.RequestId, out pending);
        if (pending is null)
        {
            // 취소 직후 도착한 청크. 교수자에게 취소가 이미 전달됐으므로 버린다.
            return;
        }

        try
        {
            await pending.Chunks.Writer.WriteAsync(chunk, cancellationToken);
            // 순서·길이·해시 검증은 저장기가 한다. 마지막 인덱스를 넘기면 스트림을 닫아 EOF 확인을 맡긴다.
            if (chunk.Index >= pending.File.TotalChunks - 1)
                pending.Chunks.Writer.TryComplete();
        }
        catch (ChannelClosedException)
        {
            // 저장기가 이미 실패/취소로 끝난 요청.
        }
    }

    private void ApplyFailure(CollaborationFailureNotice failure)
    {
        PendingDownload? pending;
        lock (_gate)
        {
            if (!_pending.TryGetValue(failure.RequestId, out pending)) return;
            pending.EndedByServer = true;
        }
        pending.Chunks.Writer.TryComplete(new CollaborationException(failure.Error));
        _logSink.Write($"[FileRoute] 교수자 전송 실패: request={failure.RequestId}, 사유={failure.Error}");
    }

    private async Task SendBestEffortAsync<T>(T message) where T : notnull
    {
        try
        {
            await _server.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), message));
        }
        catch (Exception ex)
        {
            _logSink.Write($"[FileRoute] {typeof(T).Name} 전달 실패: {ex.GetType().Name}");
        }
    }
}
