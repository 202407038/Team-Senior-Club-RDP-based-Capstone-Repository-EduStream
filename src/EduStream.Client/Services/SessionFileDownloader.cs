using System.IO;
using System.Security.Cryptography;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;

namespace EduStream.Client.Services;

/// <summary>
/// 요청별 디스크 스트리밍 수신. 해시/EOF 확인 후 원자적 이름 확보, 기존 파일은 덮어쓰지 않습니다.
/// 레거시 FileReceiver와 분리하여 현재 자동 전송 동작은 바꾸지 않습니다.
/// </summary>
public sealed class SessionFileDownloader : ISessionFileDownloader
{
    private readonly IDownloadsDirectory _directory;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _active = new();

    public SessionFileDownloader(IDownloadsDirectory? directory = null) =>
        _directory = directory ?? new WindowsDownloadsDirectory();

    public async Task<DownloadReceipt> SaveAsync(SessionFileDescriptor file, SessionFileRequest request,
        IAsyncEnumerable<SessionFileChunk> chunks, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(chunks);
        file.Validate();
        request.Validate();
        if (request.SessionId != file.SessionId || request.FileId != file.FileId ||
            request.Revision != file.Revision)
            throw new CollaborationException(CollaborationError.InvalidRequest);
        lock (_gate)
        {
            if (_active.Count >= SessionFileLimits.MaxConcurrentTransfers)
                throw new CollaborationException(CollaborationError.ResourceLimit);
            if (!_active.Add(request.RequestId))
                throw new CollaborationException(CollaborationError.InvalidRequest);
        }
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetFullPath(_directory.GetPath());
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".edustream-" + Guid.NewGuid().ToString("N") + ".partial");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, file.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                long received = 0;
                var index = 0;
                await foreach (var chunk in chunks.WithCancellation(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var expected = (int)Math.Min(file.ChunkSize, file.Length - received);
                    if (chunk is null || chunk.RequestId != request.RequestId || chunk.SessionId != file.SessionId ||
                        chunk.FileId != file.FileId || chunk.Revision != file.Revision || chunk.Index != index ||
                        index >= file.TotalChunks || chunk.Content is null || chunk.Content.Length != expected)
                        throw new CollaborationException(CollaborationError.InvalidRequest);
                    await output.WriteAsync(chunk.Content, cancellationToken);
                    hasher.AppendData(chunk.Content);
                    received += chunk.Content.Length;
                    index++;
                    progress?.Report(new(request.RequestId, received, file.Length));
                }
                if (received != file.Length || index != file.TotalChunks)
                    throw new CollaborationException(CollaborationError.TransferIncomplete);
                if (!string.Equals(Convert.ToHexString(hasher.GetHashAndReset()), file.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                    throw new CollaborationException(CollaborationError.IntegrityFailure);
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Move(overwrite:false)가 완료 경계. 그 이후 취소/등록 해제는 이미 저장한 파일을 삭제하지 않습니다.
            var path = CommitWithoutOverwrite(temporary, directory, file.FileName, cancellationToken);
            temporary = null;
            return new(request.RequestId, file.FileId, path, file.Length, file.Sha256);
        }
        finally
        {
            try
            {
                if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            }
            finally
            {
                lock (_gate) _active.Remove(request.RequestId);
            }
        }
    }

    private static string CommitWithoutOverwrite(string source, string directory, string name, CancellationToken token)
    {
        for (var suffix = 0; suffix <= 1000; suffix++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = suffix == 0 ? name :
                $"{Path.GetFileNameWithoutExtension(name)} ({suffix}){Path.GetExtension(name)}";
            var target = Path.Combine(directory, candidate);
            try
            {
                File.Move(source, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target))
            {
                // 다른 요청이 이름을 먼저 확보한 경우에만 다음 이름으로 재시도.
            }
        }
        throw new CollaborationException(CollaborationError.ResourceLimit);
    }
}
