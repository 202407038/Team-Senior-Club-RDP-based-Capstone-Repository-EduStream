using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// WDS 렌더러/컨트롤에 뷰포트 및 줌 기능을 연결하는 어댑터
/// IWheelScrollAdapter와 IViewportFitAdapter의 계산 결과를 실제 WDS 렌더러에 적용
/// 원본 비율 유지, 가시 영역 크롭(Crop) 및 뷰어 렌더링 영역 계산 구현
/// </summary>
public sealed class WdsViewportAdapter : IWdsViewportAdapter
{
    private readonly IWheelScrollAdapter _wheelScrollAdapter;
    private readonly IViewportFitAdapter _viewportFitAdapter;
    private readonly List<Func<ViewportInfo, Task>> _viewerHandlers = new();

    private double _currentZoom = 1.0;
    private Size _currentViewportSize = new(0, 0);
    private Size _sourceSize = new(0, 0);
    private Rectangle _currentSourceRect = Rectangle.Empty;
    private ViewportInfo _lastViewportInfo = new()
    {
        ViewportSize = new Size(0, 0),
        ZoomLevel = 1.0,
        SourceRect = Rectangle.Empty
    };

    // 🎯 [피드백 7번 반영] 실제 ActiveX 뷰어 참조
    private dynamic? _axViewer;

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
        UpdateSourceCropRect();
    }

    /// <summary>
    /// 뷰포트 크기 설정 (WDS 렌더러 창 크기 변경 시 호출)
    /// </summary>
    public void SetViewportSize(Size viewportSize)
    {
        _currentViewportSize = viewportSize;
    }

    /// <summary>
    /// 화면 맞춤 모드 적용 (Fit, FitWidth, FitHeight, Stretch, Original)
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
    /// 휠 스크롤로 줌 적용 (0.5x ~ 3.0x 범위 클램핑 및 가시 영역 크롭 갱신)
    /// </summary>
    public double ApplyWheelZoom(int wheelDelta)
    {
        var newZoom = _wheelScrollAdapter.CalculateZoomScale(wheelDelta, _currentZoom);
        _currentZoom = newZoom;

        if (_sourceSize.Width > 0 && _sourceSize.Height > 0)
        {
            _currentViewportSize = _viewportFitAdapter.CalculateZoomedViewport(_sourceSize, _currentZoom);
        }

        UpdateSourceCropRect();
        NotifyViewportChanged();
        return newZoom;
    }

    /// <summary>
    /// 직접 줌 배율 설정
    /// </summary>
    public void SetZoomLevel(double zoomLevel)
    {
        _currentZoom = zoomLevel;

        if (_sourceSize.Width > 0 && _sourceSize.Height > 0)
        {
            _currentViewportSize = _viewportFitAdapter.CalculateZoomedViewport(_sourceSize, _currentZoom);
        }

        UpdateSourceCropRect();
        NotifyViewportChanged();
    }

    /// <summary>
    /// 뷰포트 내 마우스/터치 좌표 유효성 확인
    /// </summary>
    public bool IsValidPoint(Point point)
    {
        return _viewportFitAdapter.IsValidViewportPoint(point, _currentViewportSize);
    }

    /// <summary>
    /// 뷰어 화면 좌표를 학생 원본 데스크톱 절대 좌표로 역변환
    /// </summary>
    public Point TranslateViewportToSource(Point viewportPoint)
    {
        if (_currentViewportSize.Width <= 0 || _currentViewportSize.Height <= 0 || _sourceSize.Width <= 0 || _sourceSize.Height <= 0)
        {
            return Point.Empty;
        }

        double ratioX = (double)_currentSourceRect.Width / _currentViewportSize.Width;
        double ratioY = (double)_currentSourceRect.Height / _currentViewportSize.Height;

        int srcX = _currentSourceRect.X + (int)(viewportPoint.X * ratioX);
        int srcY = _currentSourceRect.Y + (int)(viewportPoint.Y * ratioY);

        return new Point(
            Math.Clamp(srcX, 0, _sourceSize.Width),
            Math.Clamp(srcY, 0, _sourceSize.Height)
        );
    }

    /// <summary>
    /// 뷰어 컨테이너 내에서 종횡비를 유지하며 중앙 정렬(Letterbox/Pillarbox)되는 렌더 사각형 계산
    /// </summary>
    public Rectangle CalculateRenderBounds(Size containerSize)
    {
        if (containerSize.Width <= 0 || containerSize.Height <= 0 || _sourceSize.Width <= 0 || _sourceSize.Height <= 0)
        {
            return Rectangle.Empty;
        }

        double scaleX = (double)containerSize.Width / _sourceSize.Width;
        double scaleY = (double)containerSize.Height / _sourceSize.Height;
        double fitScale = Math.Min(scaleX, scaleY) * _currentZoom;

        int renderWidth = (int)(_sourceSize.Width * fitScale);
        int renderHeight = (int)(_sourceSize.Height * fitScale);

        int offsetX = (containerSize.Width - renderWidth) / 2;
        int offsetY = (containerSize.Height - renderHeight) / 2;

        return new Rectangle(offsetX, offsetY, renderWidth, renderHeight);
    }

    /// <summary>
    /// 현재 배율에 따른 원본 화면 가시 영역(크롭 사각형) 계산
    /// </summary>
    private void UpdateSourceCropRect()
    {
        if (_sourceSize.Width <= 0 || _sourceSize.Height <= 0)
        {
            _currentSourceRect = Rectangle.Empty;
            return;
        }

        if (_currentZoom <= 1.0)
        {
            _currentSourceRect = new Rectangle(0, 0, _sourceSize.Width, _sourceSize.Height);
        }
        else
        {
            int visibleWidth = Math.Max(1, (int)(_sourceSize.Width / _currentZoom));
            int visibleHeight = Math.Max(1, (int)(_sourceSize.Height / _currentZoom));

            int cropX = Math.Max(0, (_sourceSize.Width - visibleWidth) / 2);
            int cropY = Math.Max(0, (_sourceSize.Height - visibleHeight) / 2);

            _currentSourceRect = new Rectangle(cropX, cropY, visibleWidth, visibleHeight);
        }
    }

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
    }

    /// <summary>
    /// 등록된 WDS 뷰어 핸들러들에 계산된 뷰포트 정보 적용
    /// </summary>
    public async Task ApplyToViewerAsync(CancellationToken cancellationToken = default)
    {
        if (_viewerHandlers.Count == 0)
        {
            throw new InvalidOperationException("등록된 WDS 뷰어 핸들러가 없습니다.");
        }

        var viewportInfo = new ViewportInfo
        {
            ViewportSize = _currentViewportSize,
            ZoomLevel = _currentZoom,
            SourceRect = _currentSourceRect
        };

        foreach (var viewerHandler in _viewerHandlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await viewerHandler(viewportInfo);
        }

        ViewerApplied?.Invoke(this, new ViewerAppliedEventArgs
        {
            ViewportInfo = viewportInfo,
            AppliedAt = DateTimeOffset.UtcNow
        });
    }

    public void AddViewerHandler(Func<ViewportInfo, Task> viewerHandler)
    {
        _viewerHandlers.Add(viewerHandler ?? throw new ArgumentNullException(nameof(viewerHandler)));
    }

    public void ClearViewerHandlers()
    {
        _viewerHandlers.Clear();
    }

    /// <summary>
    /// 🎯 [피드백 7번 반영] 실제 ActiveX 뷰어 참조 설정
    /// </summary>
    public void SetAxViewer(dynamic axViewer)
    {
        _axViewer = axViewer;
    }

    /// <summary>
    /// 🎯 [피드백 7번 반영] 실제 ActiveX 뷰어에 뷰포트 설정 적용
    /// </summary>
    public void ApplyViewportSettings(Rectangle rect)
    {
        if (_axViewer != null && _axViewer.GetOcx() != null)
        {
            try
            {
                _axViewer.AdvancedSettings7.SmartSizing = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WdsViewportAdapter] ActiveX SmartSizing 설정 실패: {ex.GetType().Name}");
            }
        }
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
