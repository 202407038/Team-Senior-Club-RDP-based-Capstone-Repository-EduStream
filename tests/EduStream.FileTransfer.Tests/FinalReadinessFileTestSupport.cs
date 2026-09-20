using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;

namespace EduStream.FileTransfer.Tests;

/// <summary>테스트에서만 사용하는 모의 인증기. 제품 연결 승인 대체 금지.</summary>
internal sealed class ReadinessAuthorizer : IFileRequestAuthorizer
{
    public bool Allowed { get; set; } = true;
    public Guid ConnectionId { get; set; }
    public bool CanDownload(FileDownloadContext context, SessionFileRequest request) =>
        Allowed && context.Connection.ConnectionId == ConnectionId;
}

internal sealed class ReadinessDirectory(string path) : IDownloadsDirectory
{
    public string GetPath() => path;
}

internal sealed class ReadinessFiles : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "EduStream-readiness-" + Guid.NewGuid().ToString("N"));
    public string Downloads => Path.Combine(Root, "downloads");
    public Guid SessionId { get; } = Guid.NewGuid();
    public FileDownloadContext Context { get; }
    public ReadinessAuthorizer Authorizer { get; }
    public ReadinessFiles()
    {
        Directory.CreateDirectory(Root);
        Context = new(new(SessionId, Guid.NewGuid(), Guid.NewGuid(), ParticipantRole.Student));
        Authorizer = new() { ConnectionId = Context.Connection.ConnectionId };
    }
    public async Task<string> SourceAsync(int length, string name = "강의.bin")
    {
        var path = Path.Combine(Root, name);
        var content = new byte[length];
        new Random(42).NextBytes(content);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }
    public static SessionFileRequest Request(SessionFileDescriptor file) =>
        new(Guid.NewGuid(), file.SessionId, file.FileId, file.Revision);
    public void AssertNoPartials() =>
        Assert.Empty(Directory.Exists(Downloads) ? Directory.GetFiles(Downloads, "*.partial") : Array.Empty<string>());
    public void Dispose() => Directory.Delete(Root, true);
}
