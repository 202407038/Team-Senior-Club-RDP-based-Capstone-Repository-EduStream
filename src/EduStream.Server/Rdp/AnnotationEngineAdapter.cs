using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 판서 엔진 실제 어댑터 구현
/// 스트로크 수신, 화면 렌더러 디스패치, 원격 참가자 전송 파이프라인 중계,
/// Undo(실행 취소) 스택 관리 및 레이어 가시성 동기화 구현
/// </summary>
public sealed class AnnotationEngineAdapter : IAnnotationEngineAdapter
{
    private readonly IAnnotationManager _annotationManager;
    private readonly List<Func<AnnotationStroke, Task>> _renderPipeline = new();
    private readonly List<Func<AnnotationStroke, string, Task>> _transmissionPipeline = new();
    private readonly List<Func<AnnotationStroke, Point[], Task>> _rendererHandlers = new();
    private readonly List<Func<AnnotationLayerSnapshot, Task>> _layerSyncHandlers = new();
    private readonly Stack<IReadOnlyList<AnnotationStroke>> _undoStack = new();
    private readonly object _syncRoot = new();

    private bool _isEngineActive = false;
    private AnnotationState _currentState = AnnotationState.Empty;

    public bool IsEngineActive
    {
        get { lock (_syncRoot) return _isEngineActive; }
    }

    public AnnotationState CurrentState
    {
        get { lock (_syncRoot) return _currentState; }
    }

    public event EventHandler<EngineStateChangedEventArgs>? EngineStateChanged;
    public event EventHandler<StrokeRenderedEventArgs>? OnStrokeRendered;
    public event EventHandler<StrokeDispatchedEventArgs>? OnStrokeDispatched;
    public event EventHandler<AnnotationLayerSyncedEventArgs>? OnLayerSynced;

    public AnnotationEngineAdapter(IAnnotationManager annotationManager)
    {
        _annotationManager = annotationManager ?? throw new ArgumentNullException(nameof(annotationManager));
    }

    public Task ActivateEngineAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            _isEngineActive = true;
            _currentState = _currentState with { IsDrawing = true };
        }

        EngineStateChanged?.Invoke(this, new EngineStateChangedEventArgs
        {
            IsActive = true,
            Timestamp = DateTimeOffset.UtcNow
        });

        return Task.CompletedTask;
    }

    public Task DeactivateEngineAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            _isEngineActive = false;
            _currentState = _currentState with { IsDrawing = false };
        }

        EngineStateChanged?.Invoke(this, new EngineStateChangedEventArgs
        {
            IsActive = false,
            Timestamp = DateTimeOffset.UtcNow
        });

        return Task.CompletedTask;
    }

    public async Task ReceiveStrokeAsync(AnnotationStroke stroke, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stroke);

        lock (_syncRoot)
        {
            if (!_isEngineActive)
            {
                throw new InvalidOperationException("판서 엔진이 비활성화되어 있습니다.");
            }
        }

        var before = await _annotationManager.GetAllStrokesAsync(cancellationToken);
        lock (_syncRoot) _undoStack.Push(before.ToArray());
        if (stroke.Tool == AnnotationTool.Eraser)
        {
            foreach (var item in before.Where(item => AnnotationStrokeGeometry.HitByEraser(item, stroke)))
                await _annotationManager.DeleteStrokeAsync(item.StrokeId, cancellationToken);
            lock (_syncRoot) _currentState = _currentState.ContentChanged();
            await PushLayerSyncAsync(AnnotationLayerChange.Erased, cancellationToken);
        }
        else
        {
            await _annotationManager.AddStrokeAsync(stroke, cancellationToken);
            await RenderStrokeAsync(new StrokeRenderedEventArgs { Stroke = stroke });
            await DispatchStrokeAsync(new StrokeDispatchedEventArgs { Stroke = stroke, TargetParticipantId = string.Empty });
        }
    }

    public void AddRenderHandler(Func<AnnotationStroke, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_syncRoot)
        {
            _renderPipeline.Add(handler);
        }
    }

    public void AddTransmissionHandler(Func<AnnotationStroke, string, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_syncRoot)
        {
            _transmissionPipeline.Add(handler);
        }
    }

    public void AddRendererHandler(Func<AnnotationStroke, Point[], Task> rendererHandler)
    {
        ArgumentNullException.ThrowIfNull(rendererHandler);
        lock (_syncRoot)
        {
            _rendererHandlers.Add(rendererHandler);
        }
    }

    public void AddLayerSyncHandler(Func<AnnotationLayerSnapshot, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_syncRoot)
        {
            _layerSyncHandlers.Add(handler);
        }
    }

    public void ClearLayerSyncHandlers()
    {
        lock (_syncRoot)
        {
            _layerSyncHandlers.Clear();
        }
    }

    public void ClearRenderPipeline()
    {
        lock (_syncRoot)
        {
            _renderPipeline.Clear();
        }
    }

    public void ClearTransmissionPipeline()
    {
        lock (_syncRoot)
        {
            _transmissionPipeline.Clear();
        }
    }

    public void ClearRendererHandlers()
    {
        lock (_syncRoot)
        {
            _rendererHandlers.Clear();
        }
    }

    private async Task RenderStrokeAsync(StrokeRenderedEventArgs e)
    {
        Func<AnnotationStroke, Task>[] renderCopy;
        Func<AnnotationStroke, Point[], Task>[] rendererCopy;

        lock (_syncRoot)
        {
            if (!_isEngineActive) return;
            renderCopy = _renderPipeline.ToArray();
            rendererCopy = _rendererHandlers.ToArray();
            _currentState = _currentState.ContentChanged();
        }

        // 숨김 상태에서는 스트로크를 보존만 하고 화면에는 그리지 않는다.
        // 다시 표시하면 레이어 동기화가 보존된 스트로크를 모두 복원한다.
        if (!_annotationManager.IsLayerVisible) return;

        // 렌더러가 하나도 등록되어 있지 않으면 완료 이벤트를 발생시키지 않음
        if (renderCopy.Length == 0 && rendererCopy.Length == 0)
        {
            return;
        }

        int successCount = 0;

        foreach (var handler in renderCopy)
        {
            lock (_syncRoot) { if (!_isEngineActive) return; }
            try
            {
                await handler(e.Stroke);
                successCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AnnotationEngine] 렌더링 파이프라인 실패: {ex.Message}");
            }
        }

        var points = e.Stroke.Points?.ToArray() ?? Array.Empty<Point>();
        foreach (var rendererHandler in rendererCopy)
        {
            lock (_syncRoot) { if (!_isEngineActive) return; }
            try
            {
                await rendererHandler(e.Stroke, points);
                successCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AnnotationEngine] 실제 렌더러 핸들러 실패: {ex.Message}");
            }
        }

        lock (_syncRoot) { if (!_isEngineActive) return; }

        // 실제 1개 이상의 핸들러가 성공적으로 렌더링을 마친 경우에만 완료 이벤트 발생
        if (successCount > 0)
        {
            OnStrokeRendered?.Invoke(this, e);
        }
    }

    private async Task DispatchStrokeAsync(StrokeDispatchedEventArgs e)
    {
        Func<AnnotationStroke, string, Task>[] transmissionCopy;

        lock (_syncRoot)
        {
            if (!_isEngineActive) return;
            transmissionCopy = _transmissionPipeline.ToArray();
        }

        // 숨김 상태에서는 공유 대상 화면으로 전송하지 않는다 (재표시 시 레이어 동기화로 복원).
        if (!_annotationManager.IsLayerVisible) return;

        // 전송 핸들러가 미등록된 경우 완료 이벤트 미발생
        if (transmissionCopy.Length == 0)
        {
            return;
        }

        int successCount = 0;

        foreach (var handler in transmissionCopy)
        {
            lock (_syncRoot) { if (!_isEngineActive) return; }
            try
            {
                await handler(e.Stroke, e.TargetParticipantId);
                successCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AnnotationEngine] 전송 파이프라인 실패: {ex.Message}");
            }
        }

        lock (_syncRoot) { if (!_isEngineActive) return; }

        // 실제 전송에 성공한 경우에만 완료 이벤트 발생
        if (successCount > 0)
        {
            OnStrokeDispatched?.Invoke(this, e);
        }
    }

    public async Task SetLayerVisibilityAsync(bool isVisible, CancellationToken cancellationToken = default)
    {
        await _annotationManager.SetLayerVisibilityAsync(isVisible, cancellationToken);

        lock (_syncRoot)
        {
            _currentState = _currentState.ContentChanged() with { IsVisible = isVisible };
        }

        EngineStateChanged?.Invoke(this, new EngineStateChangedEventArgs
        {
            IsActive = IsEngineActive,
            Timestamp = DateTimeOffset.UtcNow
        });

        await PushLayerSyncAsync(AnnotationLayerChange.VisibilityChanged, cancellationToken);
    }

    public async Task ToggleLayerVisibilityAsync(CancellationToken cancellationToken = default)
    {
        var currentVisibility = _annotationManager.IsLayerVisible;
        await SetLayerVisibilityAsync(!currentVisibility, cancellationToken);
    }

    public async Task ClearAllStrokesAsync(CancellationToken cancellationToken = default)
    {
        var before = await _annotationManager.GetAllStrokesAsync(cancellationToken);
        lock (_syncRoot) _undoStack.Push(before.ToArray());
        await _annotationManager.ClearAllStrokesAsync(cancellationToken);

        lock (_syncRoot)
        {
            // 전체 지우기는 그리기 ON/OFF와 독립적이다. 지우고 OFF는 컨트롤러에서 명시한다.
            _currentState = _currentState.ContentChanged();
        }

        EngineStateChanged?.Invoke(this, new EngineStateChangedEventArgs
        {
            IsActive = IsEngineActive,
            Timestamp = DateTimeOffset.UtcNow
        });

        await PushLayerSyncAsync(AnnotationLayerChange.Cleared, cancellationToken);
    }

    public async Task UndoAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AnnotationStroke>? previous = null;
        lock (_syncRoot)
        {
            if (_undoStack.Count > 0)
            {
                previous = _undoStack.Pop();
                _currentState = _currentState.ContentChanged();
            }
        }

        if (previous != null)
        {
            // 재구성 중 개별 추가 이벤트를 송출하지 않고 최종 스냅샷 한 번으로 동기화한다.
            await _annotationManager.ClearAllStrokesAsync(cancellationToken);
            foreach (var stroke in previous)
                await _annotationManager.AddStrokeAsync(stroke, cancellationToken);

            EngineStateChanged?.Invoke(this, new EngineStateChangedEventArgs
            {
                IsActive = IsEngineActive,
                Timestamp = DateTimeOffset.UtcNow
            });

            await PushLayerSyncAsync(AnnotationLayerChange.Undone, cancellationToken);
        }
    }

    /// <summary>
    /// 지금 화면에 있어야 할 전체 스트로크(숨김이면 빈 목록)를 등록된 싱크에 밀어 넣는다.
    /// 하나 이상의 싱크가 실제로 적용에 성공했을 때만 <see cref="OnLayerSynced"/> 를 발생시킨다.
    /// </summary>
    private async Task PushLayerSyncAsync(AnnotationLayerChange change, CancellationToken cancellationToken)
    {
        Func<AnnotationLayerSnapshot, Task>[] handlers;
        long revision;
        lock (_syncRoot)
        {
            handlers = _layerSyncHandlers.ToArray();
            revision = _currentState.ContentRevision;
        }

        if (handlers.Length == 0) return;

        var isVisible = _annotationManager.IsLayerVisible;
        IReadOnlyList<AnnotationStroke> visibleStrokes = Array.Empty<AnnotationStroke>();
        if (isVisible)
        {
            var all = await _annotationManager.GetAllStrokesAsync(cancellationToken);
            visibleStrokes = all.Where(s => s.IsVisible).OrderBy(s => s.CreatedAt).ToArray();
        }

        var snapshot = new AnnotationLayerSnapshot
        {
            Change = change,
            IsVisible = isVisible,
            ContentRevision = revision,
            VisibleStrokes = visibleStrokes
        };

        int applied = 0;
        foreach (var handler in handlers)
        {
            try
            {
                await handler(snapshot);
                applied++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AnnotationEngine] 레이어 동기화 실패: {ex.Message}");
            }
        }

        if (applied > 0)
        {
            OnLayerSynced?.Invoke(this, new AnnotationLayerSyncedEventArgs
            {
                Snapshot = snapshot,
                AppliedHandlerCount = applied
            });
        }
    }

    public async Task<IReadOnlyList<AnnotationStroke>> GetStrokesByParticipantAsync(string participantId, CancellationToken cancellationToken = default)
    {
        return await _annotationManager.GetStrokesByParticipantAsync(participantId, cancellationToken);
    }

    public async Task<IReadOnlyList<AnnotationStroke>> GetAllStrokesAsync(CancellationToken cancellationToken = default)
    {
        return await _annotationManager.GetAllStrokesAsync(cancellationToken);
    }
}

/// <summary>
/// 판서 엔진 상태 변경 이벤트 인자
/// </summary>
public sealed class EngineStateChangedEventArgs : EventArgs
{
    public bool IsActive { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
