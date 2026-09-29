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
    private readonly Stack<AnnotationStroke> _undoStack = new();
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

    public AnnotationEngineAdapter(IAnnotationManager annotationManager)
    {
        _annotationManager = annotationManager ?? throw new ArgumentNullException(nameof(annotationManager));

        // AnnotationManager의 이벤트를 어댑터 파이프라인에 연결
        _annotationManager.OnStrokeRendered += OnStrokeRenderedFromManager;
        _annotationManager.OnStrokeDispatched += OnStrokeDispatchedFromManager;
    }

    /// <summary>
    /// 판서 엔진 활성화
    /// </summary>
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

    /// <summary>
    /// 판서 엔진 비활성화
    /// </summary>
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

    /// <summary>
    /// 스트로크 수신 및 유효성 검증 후 처리
    /// </summary>
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

        // AnnotationManager에 스트로크 저장 (매니저 내부 이벤트 디스패치 트리거)
        await _annotationManager.AddStrokeAsync(stroke, cancellationToken);
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

    /// <summary>
    /// 스트로크 렌더링 이벤트 핸들러 (매니저 발생 이벤트 -> 등록된 실제 렌더러 파이프라인 전달)
    /// </summary>
    private async void OnStrokeRenderedFromManager(object? sender, StrokeRenderedEventArgs e)
    {
        Func<AnnotationStroke, Task>[] renderCopy;
        Func<AnnotationStroke, Point[], Task>[] rendererCopy;

        lock (_syncRoot)
        {
            if (!_isEngineActive) return;

            _undoStack.Push(e.Stroke);
            renderCopy = _renderPipeline.ToArray();
            rendererCopy = _rendererHandlers.ToArray();
            _currentState = _currentState.ContentChanged();
        }

        // 1. 일반 렌더링 파이프라인 실행
        foreach (var handler in renderCopy)
        {
            try
            {
                await handler(e.Stroke);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AnnotationEngine] 렌더링 파이프라인 실패: {ex.Message}");
            }
        }

        // 2. 실제 화면 렌더러 핸들러 실행 (좌표 배열 전달)
        var points = e.Stroke.Points?.ToArray() ?? Array.Empty<Point>();
        foreach (var rendererHandler in rendererCopy)
        {
            try
            {
                await rendererHandler(e.Stroke, points);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AnnotationEngine] 실제 렌더러 핸들러 실패: {ex.Message}");
            }
        }

        OnStrokeRendered?.Invoke(this, e);
    }

    /// <summary>
    /// 스트로크 디스패치 이벤트 핸들러 (매니저 발생 이벤트 -> 원격 참가자 네트워크 전송 파이프라인 전달)
    /// </summary>
    private async void OnStrokeDispatchedFromManager(object? sender, StrokeDispatchedEventArgs e)
    {
        Func<AnnotationStroke, string, Task>[] transmissionCopy;

        lock (_syncRoot)
        {
            if (!_isEngineActive) return;
            transmissionCopy = _transmissionPipeline.ToArray();
        }

        foreach (var handler in transmissionCopy)
        {
            try
            {
                await handler(e.Stroke, e.TargetParticipantId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AnnotationEngine] 전송 파이프라인 실패: {ex.Message}");
            }
        }

        OnStrokeDispatched?.Invoke(this, e);
    }

    /// <summary>
    /// 판서 레이어 표시/숨김 설정
    /// </summary>
    public async Task SetLayerVisibilityAsync(bool isVisible, CancellationToken cancellationToken = default)
    {
        await _annotationManager.SetLayerVisibilityAsync(isVisible, cancellationToken);

        lock (_syncRoot)
        {
            _currentState = _currentState with { IsVisible = isVisible };
        }

        EngineStateChanged?.Invoke(this, new EngineStateChangedEventArgs
        {
            IsActive = IsEngineActive,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// 판서 레이어 표시 토글
    /// </summary>
    public async Task ToggleLayerVisibilityAsync(CancellationToken cancellationToken = default)
    {
        var currentVisibility = _annotationManager.IsLayerVisible;
        await SetLayerVisibilityAsync(!currentVisibility, cancellationToken);
    }

    /// <summary>
    /// 모든 판서 스트로크 일괄 삭제
    /// </summary>
    public async Task ClearAllStrokesAsync(CancellationToken cancellationToken = default)
    {
        await _annotationManager.ClearAllStrokesAsync(cancellationToken);

        lock (_syncRoot)
        {
            _undoStack.Clear();
            _currentState = _currentState.ClearAndStop();
        }

        EngineStateChanged?.Invoke(this, new EngineStateChangedEventArgs
        {
            IsActive = IsEngineActive,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// 마지막 스트로크 실행 취소 (Undo 스택 팝 및 매니저 동기화)
    /// </summary>
    public async Task UndoAsync(CancellationToken cancellationToken = default)
    {
        AnnotationStroke? lastStroke = null;

        lock (_syncRoot)
        {
            if (_undoStack.Count > 0)
            {
                lastStroke = _undoStack.Pop();
                _currentState = _currentState.ContentChanged();
            }
        }

        if (lastStroke != null)
        {
            await _annotationManager.DeleteStrokeAsync(lastStroke.StrokeId, cancellationToken);

            EngineStateChanged?.Invoke(this, new EngineStateChangedEventArgs
            {
                IsActive = IsEngineActive,
                Timestamp = DateTimeOffset.UtcNow
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
