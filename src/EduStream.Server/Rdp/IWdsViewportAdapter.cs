using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// WDS 뷰포트 어댑터 인터페이스
/// IWheelScrollAdapter와 IViewportFitAdapter의 계산 결과를 실제 WDS 렌더러에 적용
/// </summary>
public interface IWdsViewportAdapter
{
    /// <summary>
    /// 현재 줌 배율
    /// </summary>
    double CurrentZoom { get; }

    /// <summary>
    /// 현재 뷰포트 크기
    /// </summary>
    Size CurrentViewportSize { get; }

    /// <summary>
    /// 현재 소스 영역
    /// </summary>
    Rectangle CurrentSourceRect { get; }

    /// <summary>
    /// 뷰포트 변경 이벤트
    /// </summary>
    event EventHandler<ViewportChangedEventArgs>? ViewportChanged;

    /// <summary>
    /// WDS 뷰어 적용 이벤트 (실제 뷰어용)
    /// </summary>
    event EventHandler<ViewerAppliedEventArgs>? ViewerApplied;

    /// <summary>
    /// 원본 화면 크기 설정
    /// </summary>
    void SetSourceSize(Size sourceSize);

    /// <summary>
    /// 뷰포트 크기 설정
    /// </summary>
    void SetViewportSize(Size viewportSize);

    /// <summary>
    /// 화면 맞춤 모드 적용
    /// </summary>
    ViewportInfo ApplyFitMode(FitMode fitMode);

    /// <summary>
    /// 휠 스크롤로 줌 적용
    /// </summary>
    double ApplyWheelZoom(int wheelDelta);

    /// <summary>
    /// 직접 줌 배율 설정
    /// </summary>
    void SetZoomLevel(double zoomLevel);

    /// <summary>
    /// 뷰포트 내 좌표가 유효한지 확인
    /// </summary>
    bool IsValidPoint(Point point);

    /// <summary>
    /// WDS 뷰어에 뷰포트 적용 (실제 뷰어 메서드)
    /// </summary>
    Task ApplyToViewerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// WDS 뷰어 핸들러 추가 (실제 뷰어 적용용)
    /// </summary>
    void AddViewerHandler(Func<ViewportInfo, Task> viewerHandler);

    /// <summary>
    /// 뷰어 핸들러 초기화
    /// </summary>
    void ClearViewerHandlers();

    /// <summary>
    /// 실제 ActiveX 뷰어 참조 설정
    /// </summary>
    void SetAxViewer(dynamic axViewer);

    /// <summary>
    /// 실제 ActiveX 뷰어에 뷰포트 설정 적용
    /// </summary>
    void ApplyViewportSettings(Rectangle rect);
}

/// <summary>
/// WDS 뷰어 적용 이벤트 인자 (실제 뷰어용)
/// </summary>
public sealed class ViewerAppliedEventArgs : EventArgs
{
    public ViewportInfo ViewportInfo { get; init; } = null!;
    public DateTimeOffset AppliedAt { get; init; } = DateTimeOffset.UtcNow;
}
