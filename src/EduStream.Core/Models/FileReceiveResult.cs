using EduStream.Core.Protocols;

namespace EduStream.Core.Models;

public sealed class FileReceiveResult
{
    public OperationState State { get; set; }
    public bool Success => State == OperationState.Succeeded;
    public bool Pending => State is OperationState.Pending or OperationState.InProgress;
    public bool CanRetry { get; set; }
    public string? FilePath { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public int ReceivedChunkCount { get; set; }
    public int TotalChunks { get; set; }

    public double ProgressRatio => TotalChunks <= 0 ? (Success ? 1 : 0) : ReceivedChunkCount / (double)TotalChunks;

    public int ProgressPercent => (int)Math.Round(Math.Clamp(ProgressRatio, 0, 1) * 100);

    public FeatureOperationResult ToOperationResult(
        Guid? sessionId = null,
        Guid? correlationId = null,
        DateTimeOffset? occurredAt = null)
    {
        return FeatureOperationResult.CreateStatus(
            FeatureArea.File,
            State,
            StatusMessage,
            ProgressPercent,
            sessionId,
            correlationId,
            occurredAt,
            ErrorCode,
            CanRetry);
    }

    public static FileReceiveResult CreateSuccess(string path, string message = "파일 수신 완료", int receivedChunkCount = 0, int totalChunks = 0)
    {
        return new()
        {
            State = OperationState.Succeeded,
            FilePath = path,
            StatusMessage = message,
            ReceivedChunkCount = receivedChunkCount,
            TotalChunks = totalChunks
        };
    }

    public static FileReceiveResult CreatePending(string message, int receivedChunkCount = 0, int totalChunks = 0)
    {
        return new()
        {
            State = OperationState.InProgress,
            ErrorCode = ErrorCodes.FileChunkPending,
            ErrorMessage = message,
            StatusMessage = message,
            ReceivedChunkCount = receivedChunkCount,
            TotalChunks = totalChunks
        };
    }

    public static FileReceiveResult CreateFailure(
        string errorCode,
        string errorMessage,
        bool canRetry = false,
        int receivedChunkCount = 0,
        int totalChunks = 0)
    {
        return new()
        {
            State = OperationState.Failed,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            StatusMessage = errorMessage,
            CanRetry = canRetry,
            ReceivedChunkCount = receivedChunkCount,
            TotalChunks = totalChunks
        };
    }
}
