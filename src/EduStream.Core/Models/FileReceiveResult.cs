namespace EduStream.Core.Models;

public sealed class FileReceiveResult
{
    public bool Success { get; set; }
    public string? FilePath { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public static FileReceiveResult CreateSuccess(string path) => new() { Success = true, FilePath = path };

    public static FileReceiveResult CreateFailure(string errorCode, string errorMessage) => new() { Success = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}
