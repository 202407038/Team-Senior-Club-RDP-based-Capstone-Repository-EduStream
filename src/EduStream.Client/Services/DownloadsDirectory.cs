using System.Runtime.InteropServices;

namespace EduStream.Client.Services;

public interface IDownloadsDirectory
{
    string GetPath();
}

/// <summary>환경 변수 + Downloads 추정 대신 Windows Known Folder(리디렉션 포함)를 조회.</summary>
public sealed class WindowsDownloadsDirectory : IDownloadsDirectory
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public string GetPath()
    {
        // UI STA가 이미 초기화됐다면 유지하고, 작업자 스레드에서는 MTA를 잠시 초기화.
        var com = CoInitializeEx(IntPtr.Zero, 0);
        const int changedMode = unchecked((int)0x80010106);
        if (com < 0 && com != changedMode) Marshal.ThrowExceptionForHR(com);
        var pointer = IntPtr.Zero;
        try
        {
            var result = SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out pointer);
            Marshal.ThrowExceptionForHR(result);
            return Marshal.PtrToStringUni(pointer)
                ?? throw new InvalidOperationException("다운로드 폴더를 확인할 수 없습니다.");
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
            if (com >= 0) CoUninitialize();
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id,
        uint flags, IntPtr token, out IntPtr path);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
