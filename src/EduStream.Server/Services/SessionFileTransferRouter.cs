using System.IO;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;
using EduStream.Core.Logging;

namespace EduStream.Server.Services;

/// <summary>
/// 2번 구현: 학생 연결별 파일 목록 전달·다운로드 요청 라우팅·청크 송신·취소·저장 완료 대조를 담당합니다.
/// 청크는 요청한 연결 하나에만 순차 전송하며, 전체 청크를 큐에 쌓지 않습니다.
/// </summary>
/// <remarks>
/// 현재 단계에서는 보호 채널 계약이 없어 <see cref="AttachAsync"/>를 호출할 실제 네트워크 경로가 없습니다.
/// 채널이 확정되면 인증된 연결마다 Attach하고 수신 프레임을 <see cref="HandleFrameAsync"/>로 넘기면 됩니다.
/// </remarks>
public sealed class SessionFileTransferRouter : IDisposable
{
    private sealed class Peer(ParticipantConnection connection, ICollaborationChannel channel)
    {
        public ParticipantConnection Connection { get; } = connection;
        public ICollaborationChannel Channel { get; } = channel;
        public Dictionary<Guid, Transfer> Transfers { get; } = new(); // requestId → 진행/저장 대기 중 전송
    }

    private sealed class Transfer(SessionFileRequest request, SessionFileDescriptor file)
    {
        public SessionFileRequest Request { get; } = request;
        public SessionFileDescriptor File { get; } = file;
        // 진행 중 요청이 토큰을 계속 참조할 수 있어 Dispose하지 않는다(타이머 없는 CTS라 누수 없음).
        public CancellationTokenSource Cancellation { get; } = new();
        public bool AwaitingStored { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Peer> _peers = new(); // connectionId → peer
    private readonly ISessionFileCatalog _catalog;
    private readonly ParticipantRegistry _registry;
    private readonly ILogSink _logSink;
    private bool _disposed;

    /// <summary>
    /// 학생이 보낸 저장 완료 알림이 원 요청·파일 길이·SHA256과 모두 일치할 때만 발생합니다.
    /// </summary>
    public event Action<ParticipantConnection, FileStoredNotice>? FileStored;

    public SessionFileTransferRouter(ISessionFileCatalog catalog, ParticipantRegistry registry, ILogSink logSink)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _registry.ConnectionRemoved += OnConnectionRemoved;
    }

    /// <summary>송신 중이거나 저장 완료 알림을 기다리는 요청 수입니다.</summary>
    public int PendingTransferCount
    {
        get { lock (_gate) return _peers.Values.Sum(peer => peer.Transfers.Count); }
    }

    /// <summary>
    /// 인증된 학생 연결에 송신 채널을 붙이고 현재 파일 목록을 바로 보냅니다.
    /// 레지스트리에 살아 있는 연결과 정확히 같지 않으면 NotAuthorized입니다.
    /// </summary>
    public async Task AttachAsync(ParticipantConnection connection, ICollaborationChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(channel);
        connection.Validate();
        if (connection.Role != ParticipantRole.Student || connection.SessionId != _catalog.SessionId ||
            !IsCurrent(connection))
            throw new CollaborationException(CollaborationError.NotAuthorized);

        Peer peer;
        lock (_gate)
        {
            if (_disposed) throw new CollaborationException(CollaborationError.SessionClosed);
            if (_peers.ContainsKey(connection.ConnectionId))
                throw new CollaborationException(CollaborationError.InvalidRequest);
            peer = new Peer(connection, channel);
            _peers.Add(connection.ConnectionId, peer);
        }

        // 등록 사이에 연결이 제거됐으면 ConnectionRemoved를 놓쳤을 수 있으므로 다시 확인한다.
        if (!IsCurrent(connection))
        {
            Detach(connection.ConnectionId);
            throw new CollaborationException(CollaborationError.StaleConnection);
        }

        _logSink.Write($"[FileRoute] 채널 연결: connection={connection.ConnectionId}");
        await SendCatalogAsync(peer, cancellationToken);
    }

    /// <summary>연결의 송신 채널을 떼고 진행 중인 전송을 모두 취소합니다.</summary>
    public void Detach(Guid connectionId)
    {
        Transfer[] cancelled;
        lock (_gate)
        {
            if (!_peers.Remove(connectionId, out var peer)) return;
            cancelled = peer.Transfers.Values.ToArray();
            peer.Transfers.Clear();
        }
        foreach (var transfer in cancelled) transfer.Cancellation.Cancel();
        _logSink.Write($"[FileRoute] 채널 해제: connection={connectionId}, 취소={cancelled.Length}건");
    }

    /// <summary>
    /// 등록/해제 뒤 호출합니다. 연결된 학생 전원에게 최신 목록을 보냅니다.
    /// 한 학생의 송신 실패가 다른 학생 전달을 막지 않습니다.
    /// </summary>
    public async Task PublishCatalogAsync(CancellationToken cancellationToken = default)
    {
        Peer[] peers;
        lock (_gate)
        {
            if (_disposed) return;
            peers = _peers.Values.ToArray();
        }
        foreach (var peer in peers)
            await SendCatalogAsync(peer, cancellationToken);
    }

    /// <summary>
    /// 학생 연결에서 받은 프레임을 처리합니다. 전송 자체는 백그라운드로 돌려
    /// 수신 루프가 뒤따르는 취소/저장 완료 프레임을 계속 받을 수 있게 합니다.
    /// </summary>
    public async Task HandleFrameAsync(ParticipantConnection sender, byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(frame);

        Peer? peer;
        lock (_gate)
        {
            if (_disposed || !_peers.TryGetValue(sender.ConnectionId, out peer) || peer.Connection != sender)
                peer = null;
        }
        if (peer is null)
        {
            _logSink.Write($"[FileRoute] 채널 없는 연결의 프레임 무시: connection={sender.ConnectionId}");
            return;
        }

        try
        {
            switch (CollaborationFrameInspector.PeekKind(frame))
            {
                case CollaborationMessageKind.FileRequest:
                    await StartTransferAsync(peer, CollaborationMessageCodec.Decode<SessionFileRequest>(frame, out _));
                    break;
                case CollaborationMessageKind.FileCancel:
                    Cancel(peer, CollaborationMessageCodec.Decode<FileCancelRequest>(frame, out _));
                    break;
                case CollaborationMessageKind.FileStored:
                    CompleteStored(peer, CollaborationMessageCodec.Decode<FileStoredNotice>(frame, out _));
                    break;
                default:
                    // 목록·청크·실패 알림은 교수자만 보낸다. 학생이 보낸 것은 요청 ID를 신뢰할 수 없어 응답 없이 버린다.
                    throw new CollaborationException(CollaborationError.InvalidRequest);
            }
        }
        catch (CollaborationException ex)
        {
            _logSink.Write($"[FileRoute] 잘못된 프레임 무시: connection={sender.ConnectionId}, 사유={ex.Code}");
        }
    }

    private async Task StartTransferAsync(Peer peer, SessionFileRequest request)
    {
        var file = _catalog.GetSnapshot().Files.FirstOrDefault(
            candidate => candidate.FileId == request.FileId && candidate.Revision == request.Revision);
        CollaborationError? rejection = null;
        Transfer? transfer = null;
        lock (_gate)
        {
            if (!_peers.TryGetValue(peer.Connection.ConnectionId, out var current) || !ReferenceEquals(current, peer))
                return;
            if (request.SessionId != _catalog.SessionId || file is null)
                rejection = CollaborationError.FileUnavailable;
            else if (peer.Transfers.ContainsKey(request.RequestId))
                // 같은 요청 ID 재사용은 진행 중인 전송을 건드리지 않고 거부만 한다.
                rejection = CollaborationError.InvalidRequest;
            else if (peer.Transfers.Count >= SessionFileLimits.MaxConcurrentTransfers)
                rejection = CollaborationError.ResourceLimit;
            else
            {
                transfer = new Transfer(request, file);
                peer.Transfers.Add(request.RequestId, transfer);
            }
        }

        if (transfer is null)
        {
            await SendFailureAsync(peer, request.RequestId, rejection!.Value);
            return;
        }

        _ = RunTransferAsync(peer, transfer);
    }

    private async Task RunTransferAsync(Peer peer, Transfer transfer)
    {
        var request = transfer.Request;
        var token = transfer.Cancellation.Token;
        CollaborationError? failure = null;
        try
        {
            var context = new FileDownloadContext(peer.Connection);
            await foreach (var chunk in _catalog.DownloadAsync(context, request, token))
                await peer.Channel.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), chunk), token);

            lock (_gate)
            {
                // 송신 완료는 저장 완료가 아니다. 학생의 FileStored를 받을 때까지 성공으로 보지 않는다.
                if (peer.Transfers.TryGetValue(request.RequestId, out var current) && ReferenceEquals(current, transfer))
                    transfer.AwaitingStored = true;
            }
            _logSink.Write($"[FileRoute] 송신 완료, 저장 확인 대기: request={request.RequestId}");
            return;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logSink.Write($"[FileRoute] 전송 취소: request={request.RequestId}");
        }
        catch (CollaborationException ex)
        {
            failure = ex.Code;
        }
        catch (IOException)
        {
            // 원본 잠금·삭제 등. 로컬 경로가 담긴 예외 메시지는 학생에게 보내지 않는다.
            failure = CollaborationError.FileUnavailable;
        }
        catch (Exception ex)
        {
            // 채널 송신 실패 등. 이 연결로는 실패 알림도 갈 수 없으므로 정리만 한다.
            _logSink.Write($"[FileRoute] 전송 중단: request={request.RequestId}, {ex.GetType().Name}");
        }

        RemoveTransfer(peer, transfer);
        if (failure is { } code)
        {
            _logSink.Write($"[FileRoute] 전송 실패: request={request.RequestId}, 사유={code}");
            await SendFailureAsync(peer, request.RequestId, code);
        }
    }

    private void Cancel(Peer peer, FileCancelRequest cancel)
    {
        Transfer? transfer;
        lock (_gate)
        {
            if (cancel.SessionId != _catalog.SessionId ||
                !peer.Transfers.Remove(cancel.RequestId, out transfer))
                return;
        }
        transfer.Cancellation.Cancel();
        _logSink.Write($"[FileRoute] 학생 취소: request={cancel.RequestId}");
    }

    private void CompleteStored(Peer peer, FileStoredNotice stored)
    {
        Transfer? transfer;
        bool matches;
        lock (_gate)
        {
            if (!peer.Transfers.TryGetValue(stored.RequestId, out transfer) || !transfer.AwaitingStored)
            {
                _logSink.Write($"[FileRoute] 대기 중이 아닌 저장 완료 무시: request={stored.RequestId}");
                return;
            }
            var file = transfer.File;
            matches = stored.FileId == file.FileId && stored.Length == file.Length &&
                string.Equals(stored.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase);
            peer.Transfers.Remove(stored.RequestId);
        }

        if (!matches)
        {
            _logSink.Write($"[FileRoute] 저장 완료 정보 불일치: request={stored.RequestId}");
            return;
        }
        _logSink.Write($"[FileRoute] 학생 저장 완료: request={stored.RequestId}, file={stored.FileId}");
        FileStored?.Invoke(peer.Connection, stored);
    }

    private void RemoveTransfer(Peer peer, Transfer transfer)
    {
        lock (_gate)
        {
            if (peer.Transfers.TryGetValue(transfer.Request.RequestId, out var current) &&
                ReferenceEquals(current, transfer))
                peer.Transfers.Remove(transfer.Request.RequestId);
        }
    }

    private async Task SendCatalogAsync(Peer peer, CancellationToken cancellationToken)
    {
        try
        {
            var frame = CollaborationMessageCodec.Encode(Guid.NewGuid(), _catalog.GetSnapshot());
            await peer.Channel.SendAsync(frame, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logSink.Write($"[FileRoute] 목록 전달 실패: connection={peer.Connection.ConnectionId}, {ex.GetType().Name}");
        }
    }

    private async Task SendFailureAsync(Peer peer, Guid requestId, CollaborationError error)
    {
        try
        {
            var frame = CollaborationMessageCodec.Encode(Guid.NewGuid(), new CollaborationFailureNotice(requestId, error));
            await peer.Channel.SendAsync(frame);
        }
        catch (Exception ex)
        {
            _logSink.Write($"[FileRoute] 실패 알림 전달 실패: request={requestId}, {ex.GetType().Name}");
        }
    }

    private bool IsCurrent(ParticipantConnection connection)
    {
        var current = _registry.TryResolve(connection.ConnectionId);
        return current is { Connected: true } && current.Connection == connection;
    }

    private void OnConnectionRemoved(ParticipantConnection connection) => Detach(connection.ConnectionId);

    public void Dispose()
    {
        Guid[] connections;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            connections = _peers.Keys.ToArray();
        }
        _registry.ConnectionRemoved -= OnConnectionRemoved;
        foreach (var connectionId in connections) Detach(connectionId);
    }
}
