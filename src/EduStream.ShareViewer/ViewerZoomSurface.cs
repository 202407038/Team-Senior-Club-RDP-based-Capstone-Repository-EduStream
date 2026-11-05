using System.Drawing;
using System.Windows.Forms;

namespace EduStream.ShareViewer;

/// <summary>원본 비율 맞춤을 기본으로 하는 WDS 표시 표면. 휠 입력은 가로채지 않고 native 뷰어에 맡긴다.</summary>
public sealed class ViewerZoomSurface : Panel
{
    private Control? _viewer;
    private Size _source = new(16, 9);
    private double _zoom = 1;
    private bool _arranging;
    public double Zoom => _zoom;
    public ViewerZoomSurface()
    {
        Dock = DockStyle.Fill; BackColor = Color.Black; AutoScroll = false;
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
}
