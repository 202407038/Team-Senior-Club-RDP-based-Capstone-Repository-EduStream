using System.Drawing;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// ApplyFitMode 가 절대 스케일을 CurrentZoom 에 넣은 뒤 CalculateRenderBounds 가 다시 곱하던 회귀를 고정한다.
/// 1920×1080 을 960×540 에 맞추면 960×540 이어야 하고, 480×270 이면 안 된다.
/// </summary>
public class WdsViewportFitRegressionTests
{
    private static WdsViewportAdapter Create() => new(new WheelScrollAdapter(), new ViewportFitAdapter());

    [Fact]
    public void ApplyFitMode_ThenCalculateRenderBounds_DoesNotApplyFitScaleTwice()
    {
        var adapter = Create();
        adapter.SetSourceSize(new Size(1920, 1080));
        adapter.SetViewportSize(new Size(960, 540));

        var fit = adapter.ApplyFitMode(FitMode.Fit);
        Assert.Equal(0.5, fit.ZoomLevel, 3);
        Assert.Equal(new Size(960, 540), fit.ViewportSize);
        Assert.Equal(1.0, adapter.CurrentZoom);

        var bounds = adapter.CalculateRenderBounds(new Size(960, 540));
        Assert.Equal(new Rectangle(0, 0, 960, 540), bounds);
    }

    [Fact]
    public void Fit_ThenWheelZoomOut_ThenFitAgain_RestoresFitBounds()
    {
        var adapter = Create();
        adapter.SetSourceSize(new Size(1920, 1080));
        adapter.SetViewportSize(new Size(960, 540));

        adapter.ApplyFitMode(FitMode.Fit);
        var fitted = adapter.CalculateRenderBounds(new Size(960, 540));
        Assert.Equal(new Rectangle(0, 0, 960, 540), fitted);

        var zoom = adapter.ApplyWheelZoom(-600); // 1.0 + (-5 * 0.1) = 0.5
        Assert.Equal(0.5, zoom, 3);
        var half = adapter.CalculateRenderBounds(new Size(960, 540));
        Assert.Equal(new Rectangle(240, 135, 480, 270), half);

        adapter.ApplyFitMode(FitMode.Fit);
        var restored = adapter.CalculateRenderBounds(new Size(960, 540));
        Assert.Equal(fitted, restored);
        Assert.Equal(1.0, adapter.CurrentZoom);
    }
}
