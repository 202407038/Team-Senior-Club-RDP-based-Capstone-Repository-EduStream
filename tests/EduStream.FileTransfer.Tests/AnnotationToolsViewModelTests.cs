using EduStream.Server.ViewModels;

namespace EduStream.FileTransfer.Tests;

public sealed class AnnotationToolsViewModelTests
{
    [Fact]
    public void SettingsCanBePrepared_WithoutClaimingEngineAvailability()
    {
        var model = new AnnotationToolsViewModel();
        var changed = new List<string>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        model.Tool = "사각형";
        model.Color = "파랑";
        model.Placement = "오른쪽";
        model.StrokeWidth = 7;
        Assert.Equal("사각형", model.Tool);
        Assert.Equal("파랑", model.Color);
        Assert.Equal("오른쪽", model.Placement);
        Assert.Equal(7, model.StrokeWidth);
        Assert.Equal(new[] { "Tool", "Color", "Placement", "StrokeWidth" }, changed);
        Assert.False(model.CanExecuteDrawing);
        Assert.Contains("엔진 연결 대기", model.EngineStatus);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(100, 20)]
    [InlineData(double.NaN, 3)]
    [InlineData(double.PositiveInfinity, 3)]
    public void InvalidWidth_CannotEscapeSupportedRange(double input, double expected)
    {
        var model = new AnnotationToolsViewModel { StrokeWidth = input };
        Assert.Equal(expected, model.StrokeWidth);
    }

    [Fact]
    public void InvalidSelections_DoNotReplaceCurrentSettings()
    {
        var model = new AnnotationToolsViewModel { Tool = "unknown", Color = "", Placement = "unknown" };
        Assert.Equal("자유선", model.Tool);
        Assert.Equal("노랑", model.Color);
        Assert.Equal("하단", model.Placement);
        Assert.Equal(4, model.Placements.Count);
        Assert.Contains("지우개", model.Tools);
    }
}
