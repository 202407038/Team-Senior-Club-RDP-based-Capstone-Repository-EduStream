using System;
using System.Drawing;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// WdsViewportAdapter 단위 테스트
/// IWheelScrollAdapter와 IViewportFitAdapter의 렌더러 연동 검증
/// </summary>
public class WdsViewportAdapterTests
{
    [Fact]
    public void Constructor_ShouldInitializeWithAdapters()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();

        // Act
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);

        // Assert
        Assert.NotNull(wdsAdapter);
        Assert.Equal(1.0, wdsAdapter.CurrentZoom);
    }

    [Fact]
    public void SetSourceSize_ShouldUpdateSourceSize()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);
        var sourceSize = new Size(1920, 1080);

        // Act
        wdsAdapter.SetSourceSize(sourceSize);

        // Assert
        Assert.Equal(sourceSize, wdsAdapter.CurrentSourceRect.Size);
    }

    [Fact]
    public void SetViewportSize_ShouldUpdateViewportSize()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);
        var viewportSize = new Size(1280, 720);

        // Act
        wdsAdapter.SetViewportSize(viewportSize);

        // Assert
        Assert.Equal(viewportSize, wdsAdapter.CurrentViewportSize);
    }

    [Fact]
    public void ApplyFitMode_Fit_ShouldCalculateCorrectViewport()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);
        wdsAdapter.SetSourceSize(new Size(1920, 1080));
        wdsAdapter.SetViewportSize(new Size(1280, 720));

        // Act
        var result = wdsAdapter.ApplyFitMode(FitMode.Fit);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.ZoomLevel > 0);
        Assert.Equal(result.ZoomLevel, wdsAdapter.CurrentZoom);
    }

    [Fact]
    public void ApplyFitMode_Original_ShouldKeepOriginalSize()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);
        var sourceSize = new Size(1920, 1080);
        wdsAdapter.SetSourceSize(sourceSize);
        wdsAdapter.SetViewportSize(new Size(1280, 720));

        // Act
        var result = wdsAdapter.ApplyFitMode(FitMode.Original);

        // Assert
        Assert.Equal(1.0, result.ZoomLevel);
        Assert.Equal(sourceSize, result.ViewportSize);
    }

    [Fact]
    public void ApplyWheelZoom_PositiveDelta_ShouldIncreaseZoom()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);
        wdsAdapter.SetSourceSize(new Size(1920, 1080));
        wdsAdapter.SetViewportSize(new Size(1280, 720));
        var initialZoom = wdsAdapter.CurrentZoom;

        // Act
        var newZoom = wdsAdapter.ApplyWheelZoom(120); // WHEEL_DELTA

        // Assert
        Assert.True(newZoom > initialZoom);
        Assert.Equal(newZoom, wdsAdapter.CurrentZoom);
    }

    [Fact]
    public void ApplyWheelZoom_NegativeDelta_ShouldDecreaseZoom()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);
        wdsAdapter.SetSourceSize(new Size(1920, 1080));
        wdsAdapter.SetViewportSize(new Size(1280, 720));
        wdsAdapter.SetZoomLevel(2.0);
        var initialZoom = wdsAdapter.CurrentZoom;

        // Act
        var newZoom = wdsAdapter.ApplyWheelZoom(-120); // Negative WHEEL_DELTA

        // Assert
        Assert.True(newZoom < initialZoom);
        Assert.Equal(newZoom, wdsAdapter.CurrentZoom);
    }

    [Fact]
    public void ApplyWheelZoom_ShouldClampToMaxZoom()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);
        wdsAdapter.SetSourceSize(new Size(1920, 1080));
        wdsAdapter.SetViewportSize(new Size(1280, 720));
        wdsAdapter.SetZoomLevel(2.9);

        // Act
        var newZoom = wdsAdapter.ApplyWheelZoom(120);

        // Assert
        Assert.Equal(3.0, newZoom); // MAX_ZOOM
    }

    [Fact]
    public void ApplyWheelZoom_ShouldClampToMinZoom()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);
        wdsAdapter.SetSourceSize(new Size(1920, 1080));
        wdsAdapter.SetViewportSize(new Size(1280, 720));
        wdsAdapter.SetZoomLevel(0.6);

        // Act
        var newZoom = wdsAdapter.ApplyWheelZoom(-120);

        // Assert
        Assert.Equal(0.5, newZoom); // MIN_ZOOM
    }

    [Fact]
    public void SetZoomLevel_ShouldUpdateZoomAndViewport()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);
        wdsAdapter.SetSourceSize(new Size(1920, 1080));
        wdsAdapter.SetViewportSize(new Size(1280, 720));

        // Act
        wdsAdapter.SetZoomLevel(1.5);

        // Assert
        Assert.Equal(1.5, wdsAdapter.CurrentZoom);
        Assert.True(wdsAdapter.CurrentViewportSize.Width > 1280);
    }

    [Fact]
    public void IsValidPoint_ValidPoint_ShouldReturnTrue()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);
        wdsAdapter.SetViewportSize(new Size(1280, 720));

        // Act
        var isValid = wdsAdapter.IsValidPoint(new Point(640, 360));

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void IsValidPoint_InvalidPoint_ShouldReturnFalse()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);
        wdsAdapter.SetViewportSize(new Size(1280, 720));

        // Act
        var isValid = wdsAdapter.IsValidPoint(new Point(2000, 2000));

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ViewportChanged_ShouldRaiseEventOnViewportChange()
    {
        // Arrange
        var wheelAdapter = new WheelScrollAdapter();
        var viewportAdapter = new ViewportFitAdapter();
        var wdsAdapter = new WdsViewportAdapter(wheelAdapter, viewportAdapter);
        wdsAdapter.SetSourceSize(new Size(1920, 1080));
        wdsAdapter.SetViewportSize(new Size(1280, 720));

        ViewportChangedEventArgs? eventArgs = null;
        wdsAdapter.ViewportChanged += (sender, args) => eventArgs = args;

        // Act
        wdsAdapter.ApplyWheelZoom(120);

        // Assert
        Assert.NotNull(eventArgs);
        Assert.True(eventArgs.ZoomLevel > 1.0);
    }
}
