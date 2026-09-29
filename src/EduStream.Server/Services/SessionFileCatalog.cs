using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;
using EduStream.Core.Protocols;

namespace EduStream.Server.Services;

/// <summary>
/// 강의별 등록 목록. 원본은 수정/삭제하지 않고 요청 때 같은 해시인지 재검증합니다.
/// 참가자 인증/송신 라우팅은 2번의 IFileRequestAuthorizer와 전송 루프가 담당합니다.
/// </summary>
public sealed class SessionFileCatalog : ISessionFileCatalog
{
    private sealed record Entry(string Path, SessionFileDescriptor File);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _files = new();
    private readonly HashSet<(Guid Connection, Guid Request)> _active = new();
    private readonly IFileRequestAuthorizer _authorizer;
    private long _revision;
    private bool _disposed;

    public SessionFileCatalog(Guid sessionId, IFileRequestAuthorizer authorizer)
    {
        CollaborationContract.RequireId(sessionId, nameof(sessionId));
        SessionId = sessionId;
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
    }

    public Guid SessionId { get; }

    public async Task<SessionFileDescriptor> RegisterAsync(string localPath,
        CancellationToken cancellationToken = default)
    {
        lock (_gate) RequireOpen();
        var path = Path.GetFullPath(localPath);
        var name = Path.GetFileName(path);
        SessionFileLimits.ValidateName(name);
        await using var stream = OpenSource(path);
        if (stream.Length > SessionFileLimits.MaxFileBytes)
            throw new CollaborationException(CollaborationError.ResourceLimit);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        var file = new SessionFileDescriptor(SessionId, Guid.NewGuid(), 1, name, stream.Length,
            hash, FileTransferRules.DefaultChunkSize);
        file.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RequireOpen();
            if (_files.Count >= SessionFileLimits.MaxRegisteredFiles)
                throw new CollaborationException(CollaborationError.ResourceLimit);
            _files.Add(file.FileId, new(path, file));
            _revision++;
        }
        return file;
    }

    public bool Unregister(Guid fileId)
    {
        lock (_gate)
        {
            RequireOpen();
            if (!_files.Remove(fileId)) return false;
            _revision++;
            return true;
        }
    }

    public SessionFileCatalogSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            RequireOpen();
            return new(SessionId, _revision, _files.Values.Select(x => x.File).ToArray());
        }
    }

    public async IAsyncEnumerable<SessionFileChunk> DownloadAsync(FileDownloadContext context,
        SessionFileRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.Connection.Validate();
        request.Validate();
        CheckAccess(context, request, cancellationToken);
        var key = (context.Connection.ConnectionId, request.RequestId);
        Entry entry;
        lock (_gate)
        {
            RequireOpen();
            entry = GetEntry(request);
            if (_active.Count >= SessionFileLimits.MaxConcurrentTransfers)
                throw new CollaborationException(CollaborationError.ResourceLimit);
            if (!_active.Add(key))
                throw new CollaborationException(CollaborationError.InvalidRequest);
        }
        try
        {
            CheckAccess(context, request, cancellationToken);
            await using var stream = OpenSource(entry.Path);
            if (stream.Length != entry.File.Length)
                throw new CollaborationException(CollaborationError.SourceChanged);
            // 원본 검증이 큰 파일에서도 등록 해제/권한 철회를 늦추지 않도록 청크 사이에 확인합니다.
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[entry.File.ChunkSize];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            {
                CheckAccess(context, request, cancellationToken);
                hasher.AppendData(buffer, 0, read);
            }
            var hash = Convert.ToHexString(hasher.GetHashAndReset());
            CheckAccess(context, request, cancellationToken);
            if (!string.Equals(hash, entry.File.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CollaborationException(CollaborationError.SourceChanged);
            stream.Position = 0;
            for (var index = 0; index < entry.File.TotalChunks; index++)
            {
                CheckAccess(context, request, cancellationToken);
                var count = (int)Math.Min(entry.File.ChunkSize, entry.File.Length - stream.Position);
                var data = new byte[count];
                await stream.ReadExactlyAsync(data, cancellationToken);
                CheckAccess(context, request, cancellationToken);
                yield return new(request.RequestId, SessionId, entry.File.FileId,
                    entry.File.Revision, index, data);
            }
            CheckAccess(context, request, cancellationToken);
        }
        finally
        {
            lock (_gate) _active.Remove(key);
        }
    }

    private Entry GetEntry(SessionFileRequest request)
    {
        if (request.SessionId != SessionId ||
            !_files.TryGetValue(request.FileId, out var entry) ||
            entry.File.Revision != request.Revision)
            throw new CollaborationException(CollaborationError.FileUnavailable);
        return entry;
    }

    private void CheckAccess(FileDownloadContext context, SessionFileRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.Connection.SessionId != SessionId ||
            context.Connection.Role != ParticipantRole.Student ||
            !_authorizer.CanDownload(context, request))
            throw new CollaborationException(CollaborationError.NotAuthorized);
        lock (_gate)
        {
            RequireOpen();
            GetEntry(request);
        }
    }

    private static FileStream OpenSource(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileTransferRules.DefaultChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private void RequireOpen()
    {
        if (_disposed) throw new CollaborationException(CollaborationError.SessionClosed);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _files.Clear();
            _revision++;
        }
    }
}
