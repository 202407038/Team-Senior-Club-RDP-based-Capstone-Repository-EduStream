using System.Drawing;
using EduStream.Server.Rdp;
using Xunit;

namespace EduStream.FileTransfer.Tests;

public class MonitorDpiAdapterTests
{
    private readonly MonitorDpiAdapter _adapter = new();

    [Theory]
    [InlineData(1.0, 100, 100, 100, 100)]   // 100% DPI
    [InlineData(1.25, 100, 100, 125, 125)] // 125% DPI
    [InlineData(1.5, 100, 100, 150, 150)]  // 150% DPI
    [InlineData(2.0, 100, 100, 200, 200)]  // 200% DPI
    public void LogicalToPhysical_다양한_DPI_배율에서_정확히_변환(double scaleFactor, int logicalX, int logicalY, int expectedPhysicalX, int expectedPhysicalY)
    {
        // Arrange
        var monitor = new MonitorInfo
        {
            ScaleFactor = scaleFactor,
            DeviceName = "TestMonitor",
            DisplayName = "Test Display",
            Left = 0,
            Top = 0,
            Width = 1920,
            Height = 1080,
            DpiX = (int)(96 * scaleFactor),
            DpiY = (int)(96 * scaleFactor),
            IsPrimary = true
        };

        var logicalPoint = new Point(logicalX, logicalY);

        // Act
        var physicalPoint = _adapter.LogicalToPhysical(logicalPoint, monitor);

        // Assert
        Assert.Equal(expectedPhysicalX, physicalPoint.X);
        Assert.Equal(expectedPhysicalY, physicalPoint.Y);
    }

    [Theory]
    [InlineData(1.0, 100, 100, 100, 100)]   // 100% DPI
    [InlineData(1.25, 125, 125, 100, 100)] // 125% DPI
    [InlineData(1.5, 150, 150, 100, 100)]  // 150% DPI
    [InlineData(2.0, 200, 200, 100, 100)]  // 200% DPI
    public void PhysicalToLogical_다양한_DPI_배율에서_정확히_변환(double scaleFactor, int physicalX, int physicalY, int expectedLogicalX, int expectedLogicalY)
    {
        // Arrange
        var monitor = new MonitorInfo
        {
            ScaleFactor = scaleFactor,
            DeviceName = "TestMonitor",
            DisplayName = "Test Display",
            Left = 0,
            Top = 0,
            Width = 1920,
            Height = 1080,
            DpiX = (int)(96 * scaleFactor),
            DpiY = (int)(96 * scaleFactor),
            IsPrimary = true
        };

        var physicalPoint = new Point(physicalX, physicalY);

        // Act
        var logicalPoint = _adapter.PhysicalToLogical(physicalPoint, monitor);

        // Assert
        Assert.Equal(expectedLogicalX, logicalPoint.X);
        Assert.Equal(expectedLogicalY, logicalPoint.Y);
    }

    [Fact]
    public void LogicalToPhysical_PhysicalToLogical_변환_왕복_일치()
    {
        // Arrange
        var monitor = new MonitorInfo
        {
            ScaleFactor = 1.5, // 150% DPI
            DeviceName = "TestMonitor",
            DisplayName = "Test Display",
            Left = 0,
            Top = 0,
            Width = 1920,
            Height = 1080,
            DpiX = 144,
            DpiY = 144,
            IsPrimary = true
        };

        var originalPoint = new Point(100, 200);

        // Act
        var physical = _adapter.LogicalToPhysical(originalPoint, monitor);
        var logical = _adapter.PhysicalToLogical(physical, monitor);

        // Assert
        Assert.Equal(originalPoint.X, logical.X);
        Assert.Equal(originalPoint.Y, logical.Y);
    }

    [Fact]
    public void GetMonitors_빈_목록_아님()
    {
        // Act
        var monitors = _adapter.GetMonitors();

        // Assert
        Assert.NotEmpty(monitors);
    }

    [Fact]
    public void GetPrimaryMonitor_주_모니터_반환()
    {
        // Act
        var primaryMonitor = _adapter.GetPrimaryMonitor();

        // Assert
        Assert.NotNull(primaryMonitor);
        Assert.True(primaryMonitor.IsPrimary);
    }
}
