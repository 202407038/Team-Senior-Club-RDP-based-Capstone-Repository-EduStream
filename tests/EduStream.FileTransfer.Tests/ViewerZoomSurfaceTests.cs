using System.Drawing;
using EduStream.ShareViewer;

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
}
