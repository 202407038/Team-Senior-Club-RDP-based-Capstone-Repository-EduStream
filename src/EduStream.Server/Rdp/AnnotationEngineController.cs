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
        _ = tool; // 도구 선택은 다음 스트로크의 AnnotationStroke.Tool 로 전달된다. 엔진은 현재 도구를 보관하지 않는다.
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
}

/// <summary>
/// Core <see cref="ISharedScreenPresentation"/> 을 3번 뷰포트 어댑터에 붙이는 어댑터.
/// Fit/Zoom 은 실제 뷰어 Bounds 에 적용한다. Pan 은 WDS 뷰어에 임의 이동 API 가 없어 지원하지 않는다.
/// </summary>
public sealed class WdsSharedScreenPresentation : ISharedScreenPresentation
{
    private readonly WdsViewportAdapter _adapter;
    private readonly Func<System.Drawing.Size> _containerSize;

    public WdsSharedScreenPresentation(WdsViewportAdapter adapter, Func<System.Drawing.Size> containerSize)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _containerSize = containerSize ?? throw new ArgumentNullException(nameof(containerSize));
    }

    public Task FitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        _adapter.SetZoomLevel(factor);
        ApplyCurrent();
        return Task.CompletedTask;
    }

    public Task PanAsync(double normalizedDeltaX, double normalizedDeltaY,
        CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException(
            "WDS AxRDPViewer 는 임의 이동(Pan) API 를 제공하지 않습니다. UI_ENGINE_DEPENDENCIES.md 참고."));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void ApplyCurrent()
    {
        var container = _containerSize();
        if (container.Width <= 0 || container.Height <= 0) return;
        _adapter.ApplyViewportSettings(_adapter.CalculateRenderBounds(container));
    }
}
