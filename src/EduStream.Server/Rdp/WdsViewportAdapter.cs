using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// WDS 렌더러/컨트롤에 뷰포트 및 줌 기능을 연결하는 어댑터
/// IWheelScrollAdapter와 IViewportFitAdapter의 계산 결과를 실제 WDS 렌더러에 적용
/// 실제 WDS 뷰어에 뷰포트 적용하는 메서드 및 파이프라인 구현
/// </summary>
public sealed class WdsViewportAdapter : IWdsViewportAdapter
{
    private readonly IWheelScrollAdapter _wheelScrollAdapter;
    private readonly IViewportFitAdapter _viewportFitAdapter;
    private readonly List<Func<ViewportInfo, Task>> _viewerHandlers = new();
    private double _currentZoom = 1.0;
    private Size _currentViewportSize;
    private Size _sourceSize;
    private Rectangle _currentSourceRect;
    private ViewportInfo _lastViewportInfo = new ViewportInfo { ViewportSize = new Size(0, 0), ZoomLevel = 1.0, SourceRect = new Rectangle(0, 0, 0, 0) };

    public double CurrentZoom => _currentZoom;
    public Size CurrentViewportSize => _currentViewportSize;
    public Rectangle CurrentSourceRect => _currentSourceRect;

    public event EventHandler<ViewportChangedEventArgs>? ViewportChanged;
    public event EventHandler<ViewerAppliedEventArgs>? ViewerApplied;

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
        var viewportInfo = new ViewportInfo
        {
            ViewportSize = _currentViewportSize,
            ZoomLevel = _currentZoom,
            SourceRect = _currentSourceRect
        };
        _lastViewportInfo = viewportInfo;

        ViewportChanged?.Invoke(this, new ViewportChangedEventArgs
        {
            ZoomLevel = _currentZoom,
            ViewportSize = _currentViewportSize,
            SourceRect = _currentSourceRect,
            Timestamp = DateTimeOffset.UtcNow
        });

        // WDS 뷰어 적용 이벤트 발생 (등록된 뷰어 핸들러가 실제로 있을 때만 성공 알림 발생)
        if (_viewerHandlers.Count > 0)
        {
            ViewerApplied?.Invoke(this, new ViewerAppliedEventArgs
            {
                ViewportInfo = viewportInfo,
                AppliedAt = DateTimeOffset.UtcNow
            });
        }
    }

    /// <summary>
    /// WDS 뷰어에 뷰포트 적용 (실제 뷰어 메서드)
    /// </summary>
    public async Task ApplyToViewerAsync(CancellationToken cancellationToken = default)
    {
        // 등록된 뷰어가 없으면 적용할 대상이 없으므로 중단
        if (_viewerHandlers.Count == 0)
        {
            return;
        }
        
        var viewportInfo = new ViewportInfo
        {
            ViewportSize = _currentViewportSize,
            ZoomLevel = _currentZoom,
            SourceRect = _currentSourceRect
        };

        // 등록된 뷰어 핸들러들에게 뷰포트 정보 전달
        foreach (var viewerHandler in _viewerHandlers)
        {
            try
            {
                await viewerHandler(viewportInfo);
            }
            catch (Exception ex)
            {
                // 개별 뷰어 핸들러의 실패는 다른 핸들러에 영향을 주지 않음
                Console.WriteLine($"[WdsViewportAdapter] 뷰어 핸들러 실패: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// WDS 뷰어 핸들러 추가 (실제 뷰어 적용용)
    /// </summary>
    public void AddViewerHandler(Func<ViewportInfo, Task> viewerHandler)
    {
        _viewerHandlers.Add(viewerHandler ?? throw new ArgumentNullException(nameof(viewerHandler)));
    }

    /// <summary>
    /// 뷰어 핸들러 초기화
    /// </summary>
    public void ClearViewerHandlers()
    {
        _viewerHandlers.Clear();
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
