using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace EduStream.Client;

/// <summary>창 전환 시 현재 모니터를 유지합니다. 화면 좌표는 DIP와 섞지 않고 물리 픽셀로 처리합니다.</summary>
public static class WindowPlacement
{
    public sealed record Snapshot(Rect WorkArea, Point Position, double ScaleX, double ScaleY);

    public static Snapshot Capture(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        if (!GetWindowRect(handle, out var bounds))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var dpi = VisualTreeHelper.GetDpi(window);
        return new(new Rect(area.X, area.Y, area.Width, area.Height),
            new Point(bounds.Left, bounds.Top), dpi.DpiScaleX, dpi.DpiScaleY);
    }

    /// <summary>원래 위치를 우선 보존하고 커진 창이 작업 영역 밖으로 나가는 만큼만 이동합니다.</summary>
    public static Point ClampPosition(Rect area, Point position, Size size) => new(
        Math.Clamp(position.X, area.Left, Math.Max(area.Left, area.Right - size.Width)),
        Math.Clamp(position.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height)));

    public static void RestorePosition(Window window, Snapshot snapshot)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(handle, out var bounds)) return;
        var position = ClampPosition(snapshot.WorkArea, snapshot.Position,
            new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
        // 크기는 WPF가 결정합니다. 위치만 바꾸며 활성화/창 순서는 바꾸지 않습니다.
        SetWindowPos(handle, IntPtr.Zero, (int)position.X, (int)position.Y, 0, 0,
            0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y,
        int width, int height, uint flags);
}
