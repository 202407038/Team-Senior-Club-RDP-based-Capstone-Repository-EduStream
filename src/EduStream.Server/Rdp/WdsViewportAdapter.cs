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

    // _currentZoom 은 "맞춤(fit) 대비 사용자 배율"이다. 1.0 = 컨테이너에 맞춘 100%.
    // ApplyFitMode 가 계산한 절대 스케일(예: 0.5)을 여기에 넣으면 CalculateRenderBounds 가 한 번 더 곱한다.
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
        var container = _currentViewportSize;
        var viewportInfo = _viewportFitAdapter.CalculateFitViewport(_sourceSize, container, fitMode);
        // 맞춤 결과를 기준(100%)으로 되돌린다. 이후 휠/SetZoomLevel 은 이 기준 위에서만 움직인다.
        _currentZoom = 1.0;
        _currentViewportSize = viewportInfo.ViewportSize;
        _currentSourceRect = viewportInfo.SourceRect;

        NotifyViewportChanged();
        // 호출자에게는 절대 맞춤 스케일(예: 1920→960 이면 0.5)을 그대로 돌려준다.
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

        // 컨테이너에 대한 맞춤 스케일 × 사용자 배율. ApplyFitMode 가 이미 넣은 절대 스케일을 다시 곱하지 않는다.
        double fitScale = Math.Min(
            (double)containerSize.Width / _sourceSize.Width,
            (double)containerSize.Height / _sourceSize.Height);
        double scale = fitScale * _currentZoom;

        int renderWidth = Math.Max(1, (int)(_sourceSize.Width * scale));
        int renderHeight = Math.Max(1, (int)(_sourceSize.Height * scale));

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
    /// 마지막으로 "실제 적용이 확인된" 뷰어 경계. 적용에 성공한 적이 없으면 Empty
    /// </summary>
    public Rectangle LastAppliedViewerBounds { get; private set; } = Rectangle.Empty;

    /// <summary>
    /// 실제 ActiveX 뷰어(WinForms Control)에 뷰포트를 적용하고, 적용된 값을 다시 읽어 확인합니다.
    ///  - SmartSizing=true 로 원격 화면이 컨트롤 크기에 맞춰 스케일되게 하고
    ///  - Dock 을 해제한 뒤 Bounds(위치+크기)를 rect 로 설정합니다. (Dock=Fill 이면 크기 지정이 무시되므로)
    ///  - 읽어 본 SmartSizing/Bounds 가 요청과 다르면 예외를 던지며 ViewerApplied 는 발생하지 않습니다.
    /// 뷰어를 만든 UI 스레드가 아니면 Control.Invoke 로 마샬링합니다.
    /// 컨트롤은 크기를 강제로 다시 맞추지 않는 컨테이너(예: Panel) 안에 두어야 결과가 유지됩니다.
    /// </summary>
    public void ApplyViewportSettings(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(rect), rect, "뷰포트 크기는 0보다 커야 합니다.");

        object? viewer = _axViewer;
        if (viewer == null)
            throw new InvalidOperationException("WDS Viewer가 초기화되지 않았습니다.");

        if (viewer is not System.Windows.Forms.Control control)
            throw new NotSupportedException("WDS Viewer 는 System.Windows.Forms.Control(AxRDPViewer) 이어야 합니다.");

        try
        {
            if (control.InvokeRequired)
                control.Invoke(new Action(() => ApplyAndVerify(control, rect)));
            else
                ApplyAndVerify(control, rect);
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex)
        {
            throw new NotSupportedException("현재 연결된 WDS 컨트롤에서 SmartSizing 속성을 지원하지 않습니다.", ex);
        }
        catch (InvalidOperationException)
        {
            throw; // 검증 실패 메시지를 그대로 전달
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"ActiveX 뷰포트 설정(SmartSizing) 적용 중 예외가 발생했습니다: {ex.Message}", ex);
        }

        // 🎯 실제 적용·확인에 성공한 뒤에만 완료 이벤트 발행
        LastAppliedViewerBounds = rect;
        ViewerApplied?.Invoke(this, new ViewerAppliedEventArgs
        {
            ViewportInfo = new ViewportInfo
            {
                ViewportSize = rect.Size,
                ZoomLevel = _currentZoom,
                SourceRect = _currentSourceRect
            },
            AppliedAt = DateTimeOffset.UtcNow
        });
    }

    private static void ApplyAndVerify(System.Windows.Forms.Control control, Rectangle rect)
    {
        dynamic dynamicViewer = control;
        dynamicViewer.SmartSizing = true;

        control.Dock = System.Windows.Forms.DockStyle.None;
        control.Bounds = rect;

        bool smartSizing = (bool)dynamicViewer.SmartSizing;
        if (!smartSizing)
            throw new InvalidOperationException("SmartSizing 적용을 확인하지 못했습니다. (읽은 값: false)");

        if (control.Bounds != rect)
            throw new InvalidOperationException(
                $"뷰어 경계 적용을 확인하지 못했습니다. (요청 {rect}, 실제 {control.Bounds})");
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
