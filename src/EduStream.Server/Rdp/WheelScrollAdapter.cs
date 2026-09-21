using System;

namespace EduStream.Server.Rdp;

/// <summary>
/// 마우스 휠 스크롤 배율 어댑터 구현
/// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
/// </summary>
public sealed class WheelScrollAdapter : IWheelScrollAdapter
{
    private const int WHEEL_DELTA = 120; // 표준 휠 델타 값
    private const int DEFAULT_LINES_TO_SCROLL = 3; // 기본 스크롤 라인 수

    /// <summary>
    /// 휠 델타 값을 실제 스크롤 픽셀로 변환
    /// </summary>
    public int GetScrollPixels(int wheelDelta, double currentZoom)
    {
        // 휠 델타를 줌 배율에 맞게 조정
        double adjustedDelta = wheelDelta / currentZoom;
        
        // 표준 델타(120) 기준으로 라인 수 계산
        double lines = (adjustedDelta / WHEEL_DELTA) * DEFAULT_LINES_TO_SCROLL;
        
        // 픽셀로 변환 (평균 라인 높이 16픽셀 가정)
        return (int)(lines * 16);
    }

    /// <summary>
    /// 휠 델타 값을 실제 스크롤 픽셀로 변환 (줌 배율 및 뷰포트 높이 적용)
    /// </summary>
    public int GetScrollPixels(int wheelDelta, double currentZoom, int viewportHeight)
    {
        // 휠 델타를 줌 배율에 맞게 조정
        double adjustedDelta = wheelDelta / currentZoom;
        
        // 뷰포트 높이의 일정 비율만큼 스크롤 (기본 10%)
        double scrollRatio = 0.1;
        double scrollAmount = viewportHeight * scrollRatio;
        
        // 휠 델타 방향에 따라 스크롤 양 결정
        int direction = wheelDelta > 0 ? -1 : 1;
        
        return (int)(direction * scrollAmount * Math.Abs(adjustedDelta) / WHEEL_DELTA);
    }
}
