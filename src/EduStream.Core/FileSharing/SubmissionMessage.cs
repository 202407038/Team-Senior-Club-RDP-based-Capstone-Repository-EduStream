using EduStream.Core.Collaboration;

namespace EduStream.Core.FileSharing;

public enum SubmissionAction { Query, State, Begin, Ready, Chunk, Complete, Stored, Cancel, Rejected }
public sealed record SubmissionMessage(Guid SessionId, Guid RequestId, SubmissionAction Action,
    string FileName = "", long Length = 0, string Sha256 = "", int Index = 0, byte[]? Content = null,
    bool IsOpen = false, string Detail = "")
{
    public const int ChunkBytes = 64 * 1024;
    public void Validate()
    {
        CollaborationContract.RequireId(SessionId, nameof(SessionId));
        if (!Enum.IsDefined(Action) || Detail is null || Detail.Length > 200 ||
            Content?.Length > ChunkBytes || Length < 0 || Length > SessionFileLimits.MaxFileBytes || Index < 0)
            throw new CollaborationException(CollaborationError.InvalidRequest);
        if (Action is not (SubmissionAction.Query or SubmissionAction.State))
            CollaborationContract.RequireId(RequestId, nameof(RequestId));
        if (Action == SubmissionAction.Begin)
        {
            SessionFileLimits.ValidateName(FileName);
            if (Sha256 is null || Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
                throw new CollaborationException(CollaborationError.InvalidRequest);
        }
        if (Action == SubmissionAction.Chunk && Content is null)
            throw new CollaborationException(CollaborationError.InvalidRequest);
        if (Action != SubmissionAction.Chunk && Content is { Length: > 0 })
            throw new CollaborationException(CollaborationError.InvalidRequest);
    }
}
