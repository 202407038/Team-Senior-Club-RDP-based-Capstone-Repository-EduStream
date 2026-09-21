using System.Drawing;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

public class ViewportFitAdapterTests
{
    private readonly ViewportFitAdapter _adapter = new();

    [Fact]
    public void CalculateFitViewport_Fit_모드에서_원본_비율_유지()
    {
        // Arrange
        var sourceSize = new Size(1920, 1080);
        var targetSize = new Size(1280, 720);

        // Act
        var viewportInfo = _adapter.CalculateFitViewport(sourceSize, targetSize, FitMode.Fit);

        // Assert
        Assert.Equal(0.6666666666666666, viewportInfo.ZoomLevel, 4); // 1280/1920 = 0.666...
        Assert.Equal(1280, viewportInfo.ViewportSize.Width);
        Assert.Equal(720, viewportInfo.ViewportSize.Height);
    }

    [Fact]
    public void CalculateFitViewport_FitHeight_모드에서_높이에_맞춤()
    {
        // Arrange
        var sourceSize = new Size(1920, 1080);
        var targetSize = new Size(1280, 720);

        // Act
        var viewportInfo = _adapter.CalculateFitViewport(sourceSize, targetSize, FitMode.FitHeight);

        // Assert
        Assert.Equal(0.6666666666666666, viewportInfo.ZoomLevel, 4); // 720/1080 = 0.666...
        Assert.Equal(1280, viewportInfo.ViewportSize.Width);
        Assert.Equal(720, viewportInfo.ViewportSize.Height);
    }

    [Fact]
    public void CalculateFitViewport_FitWidth_모드에서_너비에_맞춤()
    {
        // Arrange
        var sourceSize = new Size(1920, 1080);
        var targetSize = new Size(1280, 720);

        // Act
        var viewportInfo = _adapter.CalculateFitViewport(sourceSize, targetSize, FitMode.FitWidth);

        // Assert
        Assert.Equal(0.6666666666666666, viewportInfo.ZoomLevel, 4); // 1280/1920 = 0.666...
        Assert.Equal(1280, viewportInfo.ViewportSize.Width);
        Assert.Equal(720, viewportInfo.ViewportSize.Height);
    }

    [Fact]
    public void CalculateFitViewport_Original_모드에서_원본_크기_유지()
    {
        // Arrange
        var sourceSize = new Size(1920, 1080);
        var targetSize = new Size(1280, 720);

        // Act
        var viewportInfo = _adapter.CalculateFitViewport(sourceSize, targetSize, FitMode.Original);

        // Assert
        Assert.Equal(1.0, viewportInfo.ZoomLevel);
        Assert.Equal(1920, viewportInfo.ViewportSize.Width);
        Assert.Equal(1080, viewportInfo.ViewportSize.Height);
    }

    [Fact]
    public void CalculateFitViewport_Stretch_모드에서_뷰포트에_꽉_참()
    {
        // Arrange
        var sourceSize = new Size(1920, 1080);
        var targetSize = new Size(1280, 720);

        // Act
        var viewportInfo = _adapter.CalculateFitViewport(sourceSize, targetSize, FitMode.Stretch);

        // Assert
        Assert.Equal(1280, viewportInfo.ViewportSize.Width);
        Assert.Equal(720, viewportInfo.ViewportSize.Height);
    }

    [Theory]
    [InlineData(1920, 1080, 1.0, 1920, 1080)]
    [InlineData(1920, 1080, 1.5, 2880, 1620)]
    [InlineData(1920, 1080, 0.5, 960, 540)]
    public void CalculateZoomedViewport_줌_배율에_따라_정확히_변환(int sourceWidth, int sourceHeight, double zoomLevel, int expectedWidth, int expectedHeight)
    {
        // Arrange
        var sourceSize = new Size(sourceWidth, sourceHeight);

        // Act
        var zoomedSize = _adapter.CalculateZoomedViewport(sourceSize, zoomLevel);

        // Assert
        Assert.Equal(expectedWidth, zoomedSize.Width);
        Assert.Equal(expectedHeight, zoomedSize.Height);
    }

    [Fact]
    public void CalculateZoomedViewport_0_이하_줌_배율에서_예외_발생()
    {
        // Arrange
        var sourceSize = new Size(1920, 1080);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _adapter.CalculateZoomedViewport(sourceSize, 0));
        Assert.Throws<ArgumentException>(() => _adapter.CalculateZoomedViewport(sourceSize, -1.0));
    }

    [Theory]
    [InlineData(100, 100, 200, 200, true)]
    [InlineData(0, 0, 200, 200, true)]
    [InlineData(199, 199, 200, 200, true)]
    [InlineData(200, 100, 200, 200, false)]
    [InlineData(100, 200, 200, 200, false)]
    [InlineData(-1, 100, 200, 200, false)]
    public void IsValidViewportPoint_좌표_유효성_정확히_판별(int x, int y, int width, int height, bool expectedValid)
    {
        // Arrange
        var point = new Point(x, y);
        var viewportSize = new Size(width, height);

        // Act
        bool isValid = _adapter.IsValidViewportPoint(point, viewportSize);

        // Assert
        Assert.Equal(expectedValid, isValid);
    }
}
