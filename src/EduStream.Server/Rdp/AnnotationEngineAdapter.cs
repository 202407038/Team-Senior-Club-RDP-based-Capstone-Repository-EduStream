using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 판서 엔진 최소 어댑터 구현
/// 스트로크를 전달받아 렌더링/전송 파이프라인으로 넘기는 접점
/// 5번 담당(UI 도구모음/배치)과 명확히 분리된 3번 담당 판서 엔진 인터페이스
/// </summary>
public sealed class AnnotationEngineAdapter
{
    private readonly IAnnotationManager _annotationManager;
    private readonly List<Func<AnnotationStroke, Task>> _renderPipeline = new();
    private readonly List<Func<AnnotationStroke, string, Task>> _transmissionPipeline = new();
    private bool _isEngineActive = false;

    public bool IsEngineActive => _isEngineActive;

    public event EventHandler<EngineStateChangedEventArgs>? EngineStateChanged;

    public AnnotationEngineAdapter(IAnnotationManager annotationManager)
    {
        _annotationManager = annotationManager ?? throw new ArgumentNullException(nameof(annotationManager));

        // AnnotationManager의 이벤트를 파이프라인에 연결
        _annotationManager.OnStrokeRendered += OnStrokeRendered;
        _annotationManager.OnStrokeDispatched += OnStrokeDispatched;
    }

    /// <summary>
    /// 판서 엔진 활성화
    /// </summary>
    public Task ActivateEngineAsync(CancellationToken cancellationToken = default)
    {
        _isEngineActive = true;
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
        _isEngineActive = false;
        EngineStateChanged?.Invoke(this, new EngineStateChangedEventArgs
        {
            IsActive = false,
            Timestamp = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 스트로크 수신 및 처리
    /// </summary>
    public async Task ReceiveStrokeAsync(AnnotationStroke stroke, CancellationToken cancellationToken = default)
    {
        if (!_isEngineActive)
            throw new InvalidOperationException("판서 엔진이 비활성화되어 있습니다.");

        // AnnotationManager에 스트로크 추가 (이벤트 디스패치 트리거)
        await _annotationManager.AddStrokeAsync(stroke, cancellationToken);
    }

    /// <summary>
    /// 렌더링 파이프라인에 핸들러 추가
    /// </summary>
    public void AddRenderHandler(Func<AnnotationStroke, Task> handler)
    {
        _renderPipeline.Add(handler ?? throw new ArgumentNullException(nameof(handler)));
    }

    /// <summary>
    /// 전송 파이프라인에 핸들러 추가
    /// </summary>
    public void AddTransmissionHandler(Func<AnnotationStroke, string, Task> handler)
    {
        _transmissionPipeline.Add(handler ?? throw new ArgumentNullException(nameof(handler)));
    }

    /// <summary>
    /// 렌더링 파이프라인 초기화
    /// </summary>
    public void ClearRenderPipeline()
    {
        _renderPipeline.Clear();
    }

    /// <summary>
    /// 전송 파이프라인 초기화
    /// </summary>
    public void ClearTransmissionPipeline()
    {
        _transmissionPipeline.Clear();
    }

    /// <summary>
    /// 스트로크 렌더링 이벤트 핸들러
    /// </summary>
    private async void OnStrokeRendered(object? sender, StrokeRenderedEventArgs e)
    {
        if (!_isEngineActive) return;

        foreach (var handler in _renderPipeline)
        {
            try
            {
                await handler(e.Stroke);
            }
            catch (Exception ex)
            {
                // 렌더링 파이프라인의 개별 핸들러 실패는 다른 핸들러에 영향을 주지 않음
                Console.WriteLine($"[AnnotationEngine] 렌더링 핸들러 실패: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// 스트로크 디스패치 이벤트 핸들러
    /// </summary>
    private async void OnStrokeDispatched(object? sender, StrokeDispatchedEventArgs e)
    {
        if (!_isEngineActive) return;

        foreach (var handler in _transmissionPipeline)
        {
            try
            {
                await handler(e.Stroke, e.TargetParticipantId);
            }
            catch (Exception ex)
            {
                // 전송 파이프라인의 개별 핸들러 실패는 다른 핸들러에 영향을 주지 않음
                Console.WriteLine($"[AnnotationEngine] 전송 핸들러 실패: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// 판서 레이어 표시/숨김
    /// </summary>
    public async Task SetLayerVisibilityAsync(bool isVisible, CancellationToken cancellationToken = default)
    {
        await _annotationManager.SetLayerVisibilityAsync(isVisible, cancellationToken);
    }

    /// <summary>
    /// 모든 판서 스트로크 삭제
    /// </summary>
    public async Task ClearAllStrokesAsync(CancellationToken cancellationToken = default)
    {
        await _annotationManager.ClearAllStrokesAsync(cancellationToken);
    }

    /// <summary>
    /// 특정 참가자의 판서 스트로크 조회
    /// </summary>
    public async Task<IReadOnlyList<AnnotationStroke>> GetStrokesByParticipantAsync(string participantId, CancellationToken cancellationToken = default)
    {
        return await _annotationManager.GetStrokesByParticipantAsync(participantId, cancellationToken);
    }
}

/// <summary>
/// 엔진 상태 변경 이벤트 인자
/// </summary>
public sealed class EngineStateChangedEventArgs : EventArgs
{
    public bool IsActive { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
