using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using EduStream.Core.Collaboration;

namespace EduStream.Server.Rdp;

/// <summary>
/// Core <see cref="IAnnotationController"/> 를 3번 판서 엔진에 붙이는 어댑터.
/// 5번은 이 인터페이스만 바인딩하면 되고, 그리기 구현은 <see cref="AnnotationEngineAdapter"/> /
/// <see cref="AnnotationOverlayLayer"/> 가 담당한다.
/// </summary>
public sealed class AnnotationEngineController : IAnnotationController
{
    private readonly AnnotationEngineAdapter _engine;
    private uint _argb = 0xFF000000;
    private double _thickness = 2;
    public EduStream.Core.Collaboration.AnnotationTool CurrentTool { get; private set; }
    public uint CurrentArgb => _argb;

    public AnnotationEngineController(AnnotationEngineAdapter engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _engine.EngineStateChanged += (_, _) => StateChanged?.Invoke(State);
        _engine.OnLayerSynced += (_, _) => StateChanged?.Invoke(State);
    }

    public EduStream.Core.Collaboration.AnnotationState State
    {
        get
        {
            var s = _engine.CurrentState;
            return new EduStream.Core.Collaboration.AnnotationState(s.IsDrawing, s.IsVisible, s.ContentRevision);
        }
    }

    public event Action<EduStream.Core.Collaboration.AnnotationState>? StateChanged;

    public void SetDrawing(bool enabled)
    {
        if (enabled) _engine.ActivateEngineAsync().GetAwaiter().GetResult();
        else _engine.DeactivateEngineAsync().GetAwaiter().GetResult();
        StateChanged?.Invoke(State);
    }

    public void ToggleVisibility()
    {
        _engine.ToggleLayerVisibilityAsync().GetAwaiter().GetResult();
    }

    public void SelectTool(EduStream.Core.Collaboration.AnnotationTool tool, uint argbColor, double thickness)
    {
        if (!Enum.IsDefined(tool)) throw new ArgumentOutOfRangeException(nameof(tool));
        if (!double.IsFinite(thickness) || thickness <= 0 || thickness > 256)
            throw new ArgumentOutOfRangeException(nameof(thickness));
        CurrentTool = tool;
        _argb = argbColor;
        _thickness = thickness;
    }

    public void Undo() => _engine.UndoAsync().GetAwaiter().GetResult();

    public void Clear(bool stopDrawing)
    {
        _engine.ClearAllStrokesAsync().GetAwaiter().GetResult();
        if (stopDrawing) _engine.DeactivateEngineAsync().GetAwaiter().GetResult();
    }

    public byte CurrentAlpha => (byte)((_argb >> 24) & 0xFF);
    public int CurrentStrokeWidth => Math.Max(1, (int)Math.Round(_thickness));

    /// <summary>UI는 포인터 좌표만 넘긴다. 선택 도구·색·굵기는 엔진 어댑터가 적용한다.</summary>
    public Task SubmitStrokeAsync(string participantId, System.Collections.Generic.IReadOnlyList<Point> points,
        CancellationToken cancellationToken = default)
        => _engine.ReceiveStrokeAsync(CreateStroke(participantId, points), cancellationToken);

    /// <summary>미리보기는 이력에 추가하지 않고 확정 시와 같은 그리기 설정을 사용한다.</summary>
    public AnnotationStroke CreateStroke(string participantId, System.Collections.Generic.IReadOnlyList<Point> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2) throw new ArgumentException("판서에는 점이 두 개 이상 필요합니다.", nameof(points));
        return new AnnotationStroke
        {
            ParticipantId = participantId,
            Tool = CurrentTool switch
            {
                EduStream.Core.Collaboration.AnnotationTool.Pen => AnnotationTool.Pen,
                EduStream.Core.Collaboration.AnnotationTool.Line => AnnotationTool.Line,
                EduStream.Core.Collaboration.AnnotationTool.Rectangle => AnnotationTool.Rectangle,
                EduStream.Core.Collaboration.AnnotationTool.Ellipse => AnnotationTool.Circle,
                EduStream.Core.Collaboration.AnnotationTool.Eraser => AnnotationTool.Eraser,
                _ => throw new ArgumentOutOfRangeException()
            },
            Color = new AnnotationColor((byte)(_argb >> 16), (byte)(_argb >> 8), (byte)_argb, CurrentAlpha),
            StrokeWidth = CurrentStrokeWidth,
            Points = System.Linq.Enumerable.ToArray(points)
        };
    }
}

/// <summary>
/// Core <see cref="ISharedScreenPresentation"/> 을 3번 뷰포트 어댑터에 붙이는 어댑터.
/// Fit/Zoom 은 실제 뷰어 Bounds 에 적용한다. Pan 은 WDS 뷰어에 임의 이동 API 가 없어 지원하지 않는다.
/// </summary>
public sealed class WdsSharedScreenPresentation : ISharedScreenPresentation
{
    /// <summary>AxRDPViewer 가 제공하지 않는 이동. 성공으로 처리하지 않습니다.</summary>
    public const string PanNotSupportedMessage =
        "WDS AxRDPViewer에는 화면을 임의로 이동하는 API가 없습니다. 사용할 수 있는 조작은 맞춤(FitAsync)과 배율(ZoomAsync)뿐입니다.";

    private readonly WdsViewportAdapter _adapter;
    private readonly Func<System.Drawing.Size> _containerSize;
    private MonitorInfo? _sharedMonitor;

    /// <summary>항상 false. PanAsync 는 이 제약을 예외로 알립니다.</summary>
    public bool PanSupported => false;

    public WdsSharedScreenPresentation(WdsViewportAdapter adapter, Func<System.Drawing.Size> containerSize)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _containerSize = containerSize ?? throw new ArgumentNullException(nameof(containerSize));
    }

    public Task FitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _adapter.SetViewportSize(_containerSize());
        _adapter.ApplyFitMode(FitMode.Fit);
        ApplyCurrent();
        return Task.CompletedTask;
    }

    public Task ZoomAsync(double factor, double normalizedX, double normalizedY,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = normalizedX;
        _ = normalizedY;
        _adapter.SetViewportSize(_containerSize());
        _adapter.SetZoomLevel(factor);
        ApplyCurrent();
        return Task.CompletedTask;
    }

    /// <summary>이 공유가 보여주는 모니터. 좌표 변환의 원점으로 씁니다.</summary>
    public void SetSharedMonitor(MonitorInfo? sharedMonitor) => _sharedMonitor = sharedMonitor;

    /// <summary>뷰어 좌표를 공유 데스크톱 절대 좌표로 변환합니다. 논리 좌표의 배율은 교수자 뷰어 모니터를 씁니다.</summary>
    public System.Drawing.Point MapViewerPointToDesktop(System.Drawing.Point viewerPoint, bool viewerPointIsLogical = false, MonitorInfo? viewerMonitor = null)
        => _adapter.TranslateViewerPointToDesktop(viewerPoint, _sharedMonitor, viewerPointIsLogical, viewerMonitor: viewerMonitor);

    public Task PanAsync(double normalizedDeltaX, double normalizedDeltaY,
        CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException(PanNotSupportedMessage));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void ApplyCurrent()
    {
        var container = _containerSize();
        if (container.Width <= 0 || container.Height <= 0) return;
        _adapter.ApplyViewportSettings(_adapter.CalculateRenderBounds(container));
    }
}
