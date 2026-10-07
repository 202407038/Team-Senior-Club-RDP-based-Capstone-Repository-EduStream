using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EduStream.Server;

/// <summary>
/// 교수자 창을 화면 캡처에서 제외한다. 공유 중에 학생 화면을 열어도 그 화면이
/// 다른 학생에게 다시 공유되지 않게 하려는 용도다. Windows 10 2004 미만에서는 효과가 없다.
/// </summary>
internal static class CaptureExclusion
{
    private const uint WdaExcludeFromCapture = 0x11;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }
}
