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
    private bool _arranging;
    public bool WheelZoomEnabled { get; set; } = true;
    public double Zoom => _zoom;
    public ViewerZoomSurface()
    {
        Dock = DockStyle.Fill; BackColor = Color.Black; AutoScroll = false;
        Application.AddMessageFilter(this);
        System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage += FilterWpfMessage;
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
        if (_arranging || _viewer is null || _viewer.IsDisposed) return;
        _arranging = true;
        try
        {
            // 이 표면은 테두리 없는 Panel이다. 스크롤바가 차지한 ClientSize를 기준으로
            // 다시 확대하면 Resize마다 배율이 줄어든다. 바깥 표면 크기를 기준으로 고정한다.
            var fitted = FitSize(_source, Size, _zoom);
            if (fitted.IsEmpty) return;
            if (_zoom == 1)
            {
                AutoScrollPosition = Point.Empty;
                AutoScrollMinSize = Size.Empty;
                AutoScroll = false;
            }
            else
            {
                AutoScroll = true;
                AutoScrollMinSize = fitted;
            }
            _viewer.Bounds = new Rectangle(
                Math.Max(0, (ClientSize.Width - fitted.Width) / 2) + AutoScrollPosition.X,
                Math.Max(0, (ClientSize.Height - fitted.Height) / 2) + AutoScrollPosition.Y,
                fitted.Width, fitted.Height);
        }
        finally { _arranging = false; }
    }
    public bool PreFilterMessage(ref Message m)
    {
        if (!WheelZoomEnabled || m.Msg != 0x020A || !Visible || !IsHandleCreated ||
            _viewer is null || !_viewer.IsHandleCreated || m.HWnd == IntPtr.Zero) return false;
        // WPF 호스트에 포커스가 있으면 휠 메시지는 뷰어가 아닌 부모 창으로 온다.
        // 같은 창 안에서 실제 포인터가 가리키는 표시 표면만 확대한다.
        if (GetAncestor(m.HWnd, 2) != GetAncestor(Handle, 2)) return false;
        var position = new Point(unchecked((short)((long)m.LParam & 0xffff)),
            unchecked((short)(((long)m.LParam >> 16) & 0xffff)));
        if (!RectangleToScreen(ClientRectangle).Contains(position)) return false;
        var delta = unchecked((short)(((long)m.WParam >> 16) & 0xffff));
        if (delta == 0) return false;
        SetZoom(_zoom * Math.Pow(1.15, delta / 120.0));
        return true;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage -= FilterWpfMessage;
        }
        base.Dispose(disposing);
    }
    private void FilterWpfMessage(ref System.Windows.Interop.MSG message, ref bool handled)
    {
        if (handled) return;
        var formsMessage = Message.Create(message.hwnd, message.message, message.wParam, message.lParam);
        if (PreFilterMessage(ref formsMessage)) handled = true;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
