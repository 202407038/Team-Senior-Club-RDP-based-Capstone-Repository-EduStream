using System.Diagnostics;
using System.IO;

namespace EduStream.Client.Services;

public interface IDownloadLocationOpener
{
    void OpenFolder(string path);
    void RevealFile(string path);
}

/// <summary>파일을 실행하지 않고 탐색기에서 저장 위치를 보여 준다.</summary>
public sealed class DownloadLocationOpener : IDownloadLocationOpener
{
    public void OpenFolder(string path) => Open(path, selectFile: false);
    public void RevealFile(string path) => Open(path, selectFile: true);

    public static ProcessStartInfo CreateStartInfo(string path, bool selectFile)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new IOException("저장 위치의 절대 경로를 확인할 수 없습니다.");
        var fullPath = Path.GetFullPath(path);
        if (selectFile ? !File.Exists(fullPath) : !Directory.Exists(fullPath))
            throw new FileNotFoundException("파일이나 폴더가 이동 또는 삭제되었습니다.");
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        { UseShellExecute = false };
        if (selectFile) start.ArgumentList.Add("/select,");
        start.ArgumentList.Add(fullPath);
        return start;
    }

    private static void Open(string path, bool selectFile)
    {
        using var process = Process.Start(CreateStartInfo(path, selectFile));
        if (process is null) throw new IOException("탐색기를 열지 못했습니다.");
    }
}
