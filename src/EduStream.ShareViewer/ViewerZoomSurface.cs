using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EduStream.ShareViewer;

/// <summary>원본 비율 맞춤을 기본으로 하는 WDS 표시 표면. 휠 확대 후 스크롤로 이동하며 1배에서 전체 화면으로 복귀한다.</summary>
public sealed class ViewerZoomSurface : Panel, IMessageFilter
{
    private Control? _viewer;
    private Size _source = new(16, 9);
    private double _zoom = 1;
    public bool WheelZoomEnabled { get; set; } = true;
    public double Zoom => _zoom;
    public ViewerZoomSurface()
    {
        Dock = DockStyle.Fill; BackColor = Color.Black; AutoScroll = true;
        Application.AddMessageFilter(this);
        Resize += (_, _) => ArrangeViewer();
    }
    public void Attach(Control viewer)
    {
        _viewer = viewer; viewer.Dock = DockStyle.None;
        if (viewer.Parent != this) Controls.Add(viewer);
        ArrangeViewer();
    }
    public void SetSourceSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        _source = new Size(width, height); ArrangeViewer();
    }
    public void Fit() { _zoom = 1; AutoScrollPosition = Point.Empty; ArrangeViewer(); }
    public void SetZoom(double zoom)
    {
        if (!double.IsFinite(zoom)) throw new ArgumentOutOfRangeException(nameof(zoom));
        _zoom = Math.Clamp(zoom, 1, 4); ArrangeViewer();
    }
    public static Size FitSize(Size source, Size container, double zoom = 1)
    {
        if (source.Width <= 0 || source.Height <= 0 || container.Width <= 0 || container.Height <= 0) return Size.Empty;
        if (!double.IsFinite(zoom) || zoom < 1 || zoom > 4) throw new ArgumentOutOfRangeException(nameof(zoom));
        var scale = Math.Min((double)container.Width / source.Width, (double)container.Height / source.Height) * zoom;
        return new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
    }
    private void ArrangeViewer()
    {
        if (_viewer is null || _viewer.IsDisposed) return;
        var fitted = FitSize(_source, ClientSize, _zoom);
        if (fitted.IsEmpty) return;
        AutoScrollMinSize = _zoom > 1 ? fitted : Size.Empty;
        _viewer.Bounds = new Rectangle(
            Math.Max(0, (ClientSize.Width - fitted.Width) / 2) + AutoScrollPosition.X,
            Math.Max(0, (ClientSize.Height - fitted.Height) / 2) + AutoScrollPosition.Y,
            fitted.Width, fitted.Height);
    }
    public bool PreFilterMessage(ref Message m)
    {
        if (!WheelZoomEnabled || m.Msg != 0x020A || _viewer is null || !_viewer.IsHandleCreated ||
            (m.HWnd != _viewer.Handle && !IsChild(_viewer.Handle, m.HWnd))) return false;
        var delta = unchecked((short)(((long)m.WParam >> 16) & 0xffff));
        SetZoom(_zoom * Math.Pow(1.15, delta / 120.0));
        return true;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Application.RemoveMessageFilter(this);
        base.Dispose(disposing);
    }
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
}
