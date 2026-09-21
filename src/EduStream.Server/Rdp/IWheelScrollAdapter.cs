using System.Drawing;

namespace EduStream.Server.Rdp;

/// <summary>
/// 마우스 휠 스크롤 배율 어댑터 인터페이스
/// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
/// </summary>
public interface IWheelScrollAdapter
{
    /// <summary>
    /// 휠 델타 값을 실제 스크롤 픽셀로 변환
    /// </summary>
    /// <param name="wheelDelta">휠 델타 값 (WM_MOUSEWHEEL의 wParam 상위 16비트)</param>
    /// <param name="currentZoom">현재 줌 배율 (1.0 = 100%)</param>
    /// <returns>스크롤할 픽셀 양 (양수=아래로, 음수=위로)</returns>
    int GetScrollPixels(int wheelDelta, double currentZoom);

    /// <summary>
    /// 휠 델타 값을 실제 스크롤 픽셀로 변환 (줌 배율 적용)
    /// </summary>
    /// <param name="wheelDelta">휠 델타 값</param>
    /// <param name="currentZoom">현재 줌 배율</param>
    /// <param name="viewportHeight">뷰포트 높이</param>
    /// <returns>스크롤할 픽셀 양</returns>
    int GetScrollPixels(int wheelDelta, double currentZoom, int viewportHeight);
}

/// <summary>
/// 뷰포트 맞춤 어댑터 인터페이스
/// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
/// </summary>
public interface IViewportFitAdapter
{
    /// <summary>
    /// 화면 맞춤 모드로 뷰포트 크기 계산
    /// </summary>
    /// <param name="sourceSize">원본 화면 크기</param>
    /// <param name="targetSize">타겟 뷰포트 크기</param>
    /// <param name="fitMode">맞춤 모드</param>
    /// <returns>계산된 뷰포트 크기와 줌 배율</returns>
    ViewportInfo CalculateFitViewport(Size sourceSize, Size targetSize, FitMode fitMode);

    /// <summary>
    /// 줌 배율 적용 후 뷰포트 크기 계산
    /// </summary>
    /// <param name="sourceSize">원본 화면 크기</param>
    /// <param name="zoomLevel">줌 배율 (1.0 = 100%)</param>
    /// <returns>줌 적용 후 뷰포트 크기</returns>
    Size CalculateZoomedViewport(Size sourceSize, double zoomLevel);

    /// <summary>
    /// 뷰포트 내 좌표가 유효한지 확인
    /// </summary>
    /// <param name="point">확인할 좌표</param>
    /// <param name="viewportSize">뷰포트 크기</param>
    /// <returns>유효 여부</returns>
    bool IsValidViewportPoint(Point point, Size viewportSize);
}

/// <summary>
/// 뷰포트 정보
/// </summary>
public sealed class ViewportInfo
{
    public Size ViewportSize { get; init; }
    public double ZoomLevel { get; init; }
    public Rectangle SourceRect { get; init; }
}

/// <summary>
/// 화면 맞춤 모드
/// </summary>
public enum FitMode
{
    /// <summary>
    /// 원본 비율 유지하며 전체 화면에 맞춤
    /// </summary>
    Fit,

    /// <summary>
    /// 원본 비율 유지하며 높이에 맞춤
    /// </summary>
    FitHeight,

    /// <summary>
    /// 원본 비율 유지하며 너비에 맞춤
    /// </summary>
    FitWidth,

    /// <summary>
    /// 원본 크기 그대로 표시 (잘리기 가능)
    /// </summary>
    Original,

    /// <summary>
    /// 뷰포트에 꽉 차게 늘림 (비율 변환)
    /// </summary>
    Stretch
}
