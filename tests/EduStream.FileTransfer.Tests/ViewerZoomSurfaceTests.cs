using System.Drawing;
using EduStream.ShareViewer;
using Forms = System.Windows.Forms;

namespace EduStream.FileTransfer.Tests;

public sealed class ViewerZoomSurfaceTests
{
    [Theory]
    [InlineData(1920,1080,800,600,800,450)]
    [InlineData(1080,1920,800,600,338,600)]
    [InlineData(2560,1440,640,360,640,360)]
    public void FitPreservesResolutionAspect(int sw,int sh,int cw,int ch,int ew,int eh)
        => Assert.Equal(new Size(ew,eh), ViewerZoomSurface.FitSize(new Size(sw,sh),new Size(cw,ch)));
    [Fact]
    public void ZoomAndEmptyViewport()
    {
        Assert.Equal(new Size(1600,900), ViewerZoomSurface.FitSize(new Size(1920,1080),new Size(800,600),2));
        Assert.Equal(Size.Empty, ViewerZoomSurface.FitSize(new Size(1920,1080), Size.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => ViewerZoomSurface.FitSize(new Size(1,1),new Size(1,1),double.NaN));
    }

    [Fact]
    public Task FitAfterZoomAndScroll_RemovesBarsAndRestoresFullViewport() => OnSta(() =>
    {
        using var form = new Forms.Form { ClientSize = new Size(800, 600), ShowInTaskbar = false,
            StartPosition = Forms.FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        using var surface = new ViewerZoomSurface();
        using var viewer = new Forms.Panel();
        form.Controls.Add(surface); surface.Attach(viewer); surface.SetSourceSize(1920, 1080); form.Show();
        // WPF 초기화가 프로세스 DPI 모드를 바꿀 수 있으므로 표시 후 실제 픽셀 크기를 고정한다.
        form.ClientSize = new Size(800, 600); form.PerformLayout();
        Assert.Equal(new Size(800, 600), surface.Size);
        surface.SetZoom(2);
        surface.AutoScrollPosition = new Point(200, 150);
        surface.Fit();
        Assert.Equal(1, surface.Zoom);
        Assert.False(surface.HorizontalScroll.Visible);
        Assert.False(surface.VerticalScroll.Visible);
        Assert.Equal(Point.Empty, surface.AutoScrollPosition);
        Assert.Equal(new Rectangle(0, 75, 800, 450), viewer.Bounds);
    });

    [Fact]
    public Task ZoomIsBasedOnViewport_NotShrunkenByItsOwnScrollbars() => OnSta(() =>
    {
        using var form = new Forms.Form { ClientSize = new Size(800, 600), ShowInTaskbar = false,
            StartPosition = Forms.FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        using var surface = new ViewerZoomSurface();
        using var viewer = new Forms.Panel();
        form.Controls.Add(surface); surface.Attach(viewer); surface.SetSourceSize(1920, 1080); form.Show();
        form.ClientSize = new Size(800, 600); form.PerformLayout();
        Assert.Equal(new Size(800, 600), surface.Size);
        for (var i = 0; i < 5; i++)
        {
            surface.SetZoom(2);
            Assert.Equal(new Size(1600, 900), viewer.Size);
        }
        form.ClientSize = new Size(1000, 700);
        surface.SetZoom(2);
        Assert.Equal(new Size(2000, 1125), viewer.Size);
        surface.Fit();
        Assert.False(surface.HorizontalScroll.Visible);
        Assert.False(surface.VerticalScroll.Visible);
    });

    [Fact]
    public Task WheelIsNotInterceptedOrUsedForZoom() => OnSta(() =>
    {
        using var surface = new ViewerZoomSurface();
        Assert.False(typeof(Forms.IMessageFilter).IsAssignableFrom(typeof(ViewerZoomSurface)));
        var message = new System.Windows.Interop.MSG { message = 0x020A, wParam = new IntPtr(120 << 16) };
        Assert.False(System.Windows.Interop.ComponentDispatcher.RaiseThreadMessage(ref message));
        Assert.Equal(1, surface.Zoom);
        surface.Dispose();
        Assert.False(System.Windows.Interop.ComponentDispatcher.RaiseThreadMessage(ref message));
    });

    private static Task OnSta(Action body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { body(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
}
