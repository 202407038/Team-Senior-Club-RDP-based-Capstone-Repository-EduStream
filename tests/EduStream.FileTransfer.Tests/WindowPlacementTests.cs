using System.Windows;
using EduStream.Client;

namespace EduStream.FileTransfer.Tests;

public sealed class WindowPlacementTests
{
    [Theory]
    // 원래 위치가 유효하면 이동하지 않는다.
    [InlineData(0, 0, 1920, 1040, 200, 100, 520, 600, 200, 100)]
    // 우측 보조 모니터에서도 주 모니터로 돌아가지 않는다.
    [InlineData(1920, 0, 1920, 1040, 2100, 100, 1360, 820, 2100, 100)]
    // 왼쪽/위쪽 모니터의 음수 좌표를 보존한다.
    [InlineData(-1920, 0, 1920, 1040, -1700, 100, 1360, 820, -1700, 100)]
    [InlineData(0, -1080, 1920, 1040, 200, -1000, 1360, 820, 200, -1000)]
    // 확대된 창이 넘친 만큼만 당긴다.
    [InlineData(1920, 0, 1920, 1040, 3300, 800, 1360, 820, 2480, 220)]
    // 작업 표시줄과 DPI를 반영한 물리 픽셀 영역도 동일하게 처리한다.
    [InlineData(2560, 40, 2560, 1360, 4000, 700, 2040, 1230, 3080, 170)]
    // 창이 영역보다 커도 좌상단을 유지하여 제목 표시줄 접근을 보장한다.
    [InlineData(-1280, 0, 1280, 680, -1000, 100, 1360, 820, -1280, 0)]
    public void ClampPosition_PreservesCurrentMonitorAndOnlyMovesOverflow(
        double x, double y, double width, double height, double left, double top,
        double windowWidth, double windowHeight, double expectedLeft, double expectedTop)
    {
        var result = WindowPlacement.ClampPosition(new Rect(x, y, width, height),
            new Point(left, top), new Size(windowWidth, windowHeight));
        Assert.Equal(expectedLeft, result.X);
        Assert.Equal(expectedTop, result.Y);
    }
}
