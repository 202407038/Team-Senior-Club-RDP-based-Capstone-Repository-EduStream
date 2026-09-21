using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

public class WheelScrollAdapterTests
{
    private readonly WheelScrollAdapter _adapter = new();

    [Theory]
    [InlineData(120, 1.0, 48)]   // 기본 휠, 100% 줌
    [InlineData(120, 1.5, 32)]   // 기본 휠, 150% 줌
    [InlineData(120, 2.0, 24)]   // 기본 휠, 200% 줌
    [InlineData(-120, 1.0, -48)] // 역방향 휠, 100% 줌
    [InlineData(240, 1.0, 96)]   // 더블 휠, 100% 줌
    public void GetScrollPixels_줌_배율에_따라_정확히_변환(int wheelDelta, double currentZoom, int expectedScrollPixels)
    {
        // Act
        int scrollPixels = _adapter.GetScrollPixels(wheelDelta, currentZoom);

        // Assert
        Assert.Equal(expectedScrollPixels, scrollPixels);
    }

    [Theory]
    [InlineData(120, 1.0, 1000, -100)] // 기본 휠, 100% 줌, 높이 1000
    [InlineData(120, 1.5, 1000, -66)]  // 기본 휠, 150% 줌, 높이 1000
    [InlineData(-120, 1.0, 1000, 100)] // 역방향 휠, 100% 줌, 높이 1000
    public void GetScrollPixels_뷰포트_높이_고려하여_변환(int wheelDelta, double currentZoom, int viewportHeight, int expectedScrollPixels)
    {
        // Act
        int scrollPixels = _adapter.GetScrollPixels(wheelDelta, currentZoom, viewportHeight);

        // Assert
        Assert.Equal(expectedScrollPixels, scrollPixels);
    }
}
