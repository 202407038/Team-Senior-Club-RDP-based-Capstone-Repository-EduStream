using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

public class WheelScrollAdapterTests
{
    private readonly WheelScrollAdapter _adapter = new();

    [Theory]
    [InlineData(120, 1.0, 1.1)]   // 기본 휠 위로, 100% 줌 -> 110%
    [InlineData(120, 1.5, 1.6)]   // 기본 휠 위로, 150% 줌 -> 160%
    [InlineData(-120, 1.0, 0.9)]  // 기본 휠 아래로, 100% 줌 -> 90%
    [InlineData(-120, 0.6, 0.5)]  // 기본 휠 아래로, 60% 줌 -> 50% (최소값 클램핑)
    [InlineData(120, 2.9, 3.0)]   // 기본 휠 위로, 290% 줌 -> 300% (최대값 클램핑)
    [InlineData(240, 1.0, 1.2)]   // 더블 휠 위로, 100% 줌 -> 120%
    public void CalculateZoomScale_줌_배율_범위_클램핑(int wheelDelta, double currentZoom, double expectedZoom)
    {
        // Act
        double newZoom = _adapter.CalculateZoomScale(wheelDelta, currentZoom);

        // Assert
        Assert.Equal(expectedZoom, newZoom, 2);
    }

    [Fact]
    public void CalculateZoomScale_최소_줌_0_5x_클램핑()
    {
        // Arrange
        var currentZoom = 0.5;

        // Act
        var newZoom = _adapter.CalculateZoomScale(-120, currentZoom);

        // Assert
        Assert.Equal(0.5, newZoom);
    }

    [Fact]
    public void CalculateZoomScale_최대_줌_3_0x_클램핑()
    {
        // Arrange
        var currentZoom = 3.0;

        // Act
        var newZoom = _adapter.CalculateZoomScale(120, currentZoom);

        // Assert
        Assert.Equal(3.0, newZoom);
    }
}
