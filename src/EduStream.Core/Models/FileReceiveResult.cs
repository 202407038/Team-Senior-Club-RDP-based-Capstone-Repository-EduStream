using EduStream.Core.Protocols;

namespace EduStream.Core.Models;

public sealed class FileReceiveResult
{
    public bool Success { get; set; }
    public bool Pending { get; set; }
    public string? FilePath { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public static FileReceiveResult CreateSuccess(string path) => new() { Success = true, FilePath = path };

    public static FileReceiveResult CreatePending(string message) => new() { Pending = true, ErrorCode = ErrorCodes.FileChunkPending, ErrorMessage = message };

    public static FileReceiveResult CreateFailure(string errorCode, string errorMessage) => new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}
