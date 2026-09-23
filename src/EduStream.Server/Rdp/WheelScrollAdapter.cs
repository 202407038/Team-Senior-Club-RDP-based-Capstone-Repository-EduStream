using System;

namespace EduStream.Server.Rdp;

/// <summary>
/// 마우스 휠 줌 어댑터 구현
/// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
/// </summary>
public sealed class WheelScrollAdapter : IWheelScrollAdapter
{
    private const int WHEEL_DELTA = 120; // 표준 휠 델타 값
    private const double MIN_ZOOM = 0.5; // 최소 줌 배율 (50%)
    private const double MAX_ZOOM = 3.0; // 최대 줌 배율 (300%)
    private const double ZOOM_STEP = 0.1; // 줌 스텝 (10%)

    /// <summary>
    /// 휠 델타 값을 줌 배율로 변환
    /// </summary>
    public double CalculateZoomScale(int wheelDelta, double currentZoom)
    {
        // 휠 델타 방향에 따라 줌 증감 계산
        double zoomChange = (wheelDelta / (double)WHEEL_DELTA) * ZOOM_STEP;
        double newZoom = currentZoom + zoomChange;

        // 범위 클램핑 (0.5x ~ 3.0x)
        return Math.Clamp(newZoom, MIN_ZOOM, MAX_ZOOM);
    }
}
