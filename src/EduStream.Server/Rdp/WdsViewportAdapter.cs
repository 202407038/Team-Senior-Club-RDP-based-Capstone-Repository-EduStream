using System;
using System.Drawing;

namespace EduStream.Server.Rdp;

/// <summary>
/// WDS 렌더러/컨트롤에 뷰포트 및 줌 기능을 연결하는 어댑터
/// IWheelScrollAdapter와 IViewportFitAdapter의 계산 결과를 실제 WDS 렌더러에 적용
/// </summary>
public sealed class WdsViewportAdapter
{
    private readonly IWheelScrollAdapter _wheelScrollAdapter;
    private readonly IViewportFitAdapter _viewportFitAdapter;
    private double _currentZoom = 1.0;
    private Size _currentViewportSize;
    private Size _sourceSize;
    private Rectangle _currentSourceRect;

    public double CurrentZoom => _currentZoom;
    public Size CurrentViewportSize => _currentViewportSize;
    public Rectangle CurrentSourceRect => _currentSourceRect;

    public event EventHandler<ViewportChangedEventArgs>? ViewportChanged;

    public WdsViewportAdapter(IWheelScrollAdapter wheelScrollAdapter, IViewportFitAdapter viewportFitAdapter)
    {
        _wheelScrollAdapter = wheelScrollAdapter ?? throw new ArgumentNullException(nameof(wheelScrollAdapter));
        _viewportFitAdapter = viewportFitAdapter ?? throw new ArgumentNullException(nameof(viewportFitAdapter));
    }

    /// <summary>
    /// 원본 화면 크기 설정 (WDS 렌더러 초기화 시 호출)
    /// </summary>
    public void SetSourceSize(Size sourceSize)
    {
        _sourceSize = sourceSize;
        _currentSourceRect = new Rectangle(0, 0, sourceSize.Width, sourceSize.Height);
    }

    /// <summary>
    /// 뷰포트 크기 설정 (WDS 렌더러 크기 변경 시 호출)
    /// </summary>
    public void SetViewportSize(Size viewportSize)
    {
        _currentViewportSize = viewportSize;
    }

    /// <summary>
    /// 화면 맞춤 모드 적용
    /// </summary>
    public ViewportInfo ApplyFitMode(FitMode fitMode)
    {
        var viewportInfo = _viewportFitAdapter.CalculateFitViewport(_sourceSize, _currentViewportSize, fitMode);
        _currentZoom = viewportInfo.ZoomLevel;
        _currentViewportSize = viewportInfo.ViewportSize;
        _currentSourceRect = viewportInfo.SourceRect;

        NotifyViewportChanged();
        return viewportInfo;
    }

    /// <summary>
    /// 휠 스크롤로 줌 적용
    /// </summary>
    public double ApplyWheelZoom(int wheelDelta)
    {
        var newZoom = _wheelScrollAdapter.CalculateZoomScale(wheelDelta, _currentZoom);
        _currentZoom = newZoom;
        _currentViewportSize = _viewportFitAdapter.CalculateZoomedViewport(_sourceSize, _currentZoom);

        NotifyViewportChanged();
        return newZoom;
    }

    /// <summary>
    /// 직접 줌 배율 설정
    /// </summary>
    public void SetZoomLevel(double zoomLevel)
    {
        _currentZoom = zoomLevel;
        _currentViewportSize = _viewportFitAdapter.CalculateZoomedViewport(_sourceSize, _currentZoom);

        NotifyViewportChanged();
    }

    /// <summary>
    /// 뷰포트 내 좌표 유효성 확인
    /// </summary>
    public bool IsValidPoint(Point point)
    {
        return _viewportFitAdapter.IsValidViewportPoint(point, _currentViewportSize);
    }

    /// <summary>
    /// 뷰포트 변경 이벤트 발생
    /// </summary>
    private void NotifyViewportChanged()
    {
        ViewportChanged?.Invoke(this, new ViewportChangedEventArgs
        {
            ZoomLevel = _currentZoom,
            ViewportSize = _currentViewportSize,
            SourceRect = _currentSourceRect,
            Timestamp = DateTimeOffset.UtcNow
        });
    }
}

/// <summary>
/// 뷰포트 변경 이벤트 인자
/// </summary>
public sealed class ViewportChangedEventArgs : EventArgs
{
    public double ZoomLevel { get; init; }
    public Size ViewportSize { get; init; }
    public Rectangle SourceRect { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
