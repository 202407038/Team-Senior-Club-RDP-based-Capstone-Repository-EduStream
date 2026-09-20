using EduStream.Core.Collaboration;

namespace EduStream.Core.FileSharing;

public sealed record SessionFileDescriptor(Guid SessionId, Guid FileId, long Revision,
    string FileName, long Length, string Sha256, int ChunkSize)
{
    public void Validate()
    {
        CollaborationContract.RequireId(SessionId, nameof(SessionId));
        CollaborationContract.RequireId(FileId, nameof(FileId));
        if (Revision < 1 || Length < 0 || Length > SessionFileLimits.MaxFileBytes)
            throw new CollaborationException(CollaborationError.InvalidRequest);
        SessionFileLimits.ValidateName(FileName);
        if (Sha256 is null || Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
            throw new CollaborationException(CollaborationError.InvalidRequest);
        EduStream.Core.Utils.FileTransferUtility.ValidateChunkSize(ChunkSize);
    }

    public int TotalChunks => (int)Math.Max(1, (Length + ChunkSize - 1) / ChunkSize);
}

public sealed record SessionFileRequest(Guid RequestId, Guid SessionId, Guid FileId, long Revision)
{
    public void Validate()
    {
        CollaborationContract.RequireId(RequestId, nameof(RequestId));
        CollaborationContract.RequireId(SessionId, nameof(SessionId));
        CollaborationContract.RequireId(FileId, nameof(FileId));
        if (Revision < 1) throw new CollaborationException(CollaborationError.InvalidRequest);
    }
}

/// <summary>2번이 외부 패킷과 분리해 공급하는 인증/연결 정보.</summary>
public sealed record FileDownloadContext(ParticipantConnection Connection);

public sealed record SessionFileChunk(Guid RequestId, Guid SessionId, Guid FileId, long Revision,
    int Index, byte[] Content);
public sealed record SessionFileCatalogSnapshot(Guid SessionId, long Revision,
    IReadOnlyList<SessionFileDescriptor> Files);
public sealed record DownloadProgress(Guid RequestId, long ReceivedBytes, long TotalBytes);
public sealed record DownloadReceipt(Guid RequestId, Guid FileId, string LocalPath, long Length, string Sha256);

/// <summary>2번 구현. 현재 살아 있는 인증된 연결의 세션/역할을 매번 대조, 실패 시 false.</summary>
public interface IFileRequestAuthorizer
{
    bool CanDownload(FileDownloadContext context, SessionFileRequest request);
}

/// <summary>교수자 로컬에서만 등록/해제 호출. 학생 경로 문자열 입력은 받지 않습니다.</summary>
public interface ISessionFileCatalog : IDisposable
{
    Guid SessionId { get; }
    Task<SessionFileDescriptor> RegisterAsync(string localPath, CancellationToken cancellationToken = default);
    bool Unregister(Guid fileId);
    SessionFileCatalogSnapshot GetSnapshot();
    IAsyncEnumerable<SessionFileChunk> DownloadAsync(FileDownloadContext context, SessionFileRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>4번 저장기. chunks는 2번이 요청 ID로 분리한 단일 응답 스트림.</summary>
public interface ISessionFileDownloader
{
    Task<DownloadReceipt> SaveAsync(SessionFileDescriptor file, SessionFileRequest request,
        IAsyncEnumerable<SessionFileChunk> chunks, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public static class SessionFileLimits
{
    // 새 요청형 경로의 명시적 자원 상한이며 제품 수용 인원/성능 보장이 아닙니다.
    public const long MaxFileBytes = 512L * 1024 * 1024;
    public const int MaxRegisteredFiles = 100;
    public const int MaxConcurrentTransfers = 8;

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            throw new CollaborationException(CollaborationError.InvalidRequest);
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
             (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³')))
            throw new CollaborationException(CollaborationError.InvalidRequest);
    }
}
