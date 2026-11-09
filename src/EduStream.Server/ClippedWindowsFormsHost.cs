using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;

namespace EduStream.Server;

/// <summary>연결을 끊지 않고 WPF 컨테이너와 네이티브 수신 창의 표시 영역을 함께 제한한다.</summary>
internal sealed class ClippedWindowsFormsHost : WindowsFormsHost
{
    private IntPtr _clippedHandle;
    private Rect? _lastClip;

    public ClippedWindowsFormsHost()
    {
        // 투명한 상위 배경은 이전 목록을 WinForms 배경 이미지로 복사하므로 불투명하게 지정한다.
        Background = System.Windows.Media.Brushes.Black;
        LayoutUpdated += (_, _) => UpdateNativeClip();
        IsVisibleChanged += (_, _) => UpdateNativeClip();
        Loaded += (_, _) => UpdateNativeClip();
        Unloaded += (_, _) => ApplyNativeClip(Rect.Empty);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        UpdateNativeClip();
    }

    private void UpdateNativeClip()
    {
        if (Handle == IntPtr.Zero) return;
        var clip = IsVisible && IsLoaded ? new Rect(RenderSize) : Rect.Empty;
        // HWND 자식 창에는 WPF의 스크롤·접기 표시 영역 제한이 자동 적용되지 않는다.
        for (var parent = VisualTreeHelper.GetParent(this); !clip.IsEmpty && parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is UIElement element && (element.ClipToBounds || element is ScrollContentPresenter))
            {
                var bounds = element.TransformToDescendant(this).TransformBounds(new Rect(element.RenderSize));
                clip.Intersect(bounds);
            }
        }
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        if (!clip.IsEmpty) clip.Transform(scale);
        ApplyNativeClip(clip);
    }

    private void ApplyNativeClip(Rect clip)
    {
        var handle = Handle;
        if (handle == IntPtr.Zero) return;
        // 소수 DPI 경계가 이웃 헤더를 덮지 않도록 안쪽으로 반올림한다.
        var left = clip.IsEmpty ? 0 : (int)Math.Ceiling(clip.Left);
        var top = clip.IsEmpty ? 0 : (int)Math.Ceiling(clip.Top);
        var right = clip.IsEmpty ? 0 : Math.Max(left, (int)Math.Floor(clip.Right));
        var bottom = clip.IsEmpty ? 0 : Math.Max(top, (int)Math.Floor(clip.Bottom));
        var pixels = new Rect(left, top, right - left, bottom - top);
        if (_clippedHandle == handle && _lastClip == pixels) return;
        var region = CreateRectRgn(left, top, right, bottom);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(handle, region, true) == 0)
        {
            DeleteObject(region);
            System.Diagnostics.Trace.TraceWarning("Student viewer native clipping could not be applied.");
            return;
        }
        // 성공한 영역의 소유권은 Windows로 넘어가므로 직접 해제하지 않는다.
        _clippedHandle = handle;
        _lastClip = pixels;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
