using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 판서 엔진 어댑터 인터페이스
/// 5번(UI 툴바/배치)과 명확히 분리된 3번 전용 엔진
/// 스트로크 렌더링 및 학생 전송 파이프라인 접점
/// </summary>
public interface IAnnotationEngineAdapter
{
    /// <summary>
    /// 엔진 활성 상태
    /// </summary>
    bool IsEngineActive { get; }

    /// <summary>
    /// 현재 판서 상태
    /// </summary>
    AnnotationState CurrentState { get; }

    /// <summary>
    /// 엔진 상태 변경 이벤트
    /// </summary>
    event EventHandler<EngineStateChangedEventArgs>? EngineStateChanged;

    /// <summary>
    /// 스트로크 렌더링 이벤트 (실제 렌더러용)
    /// </summary>
    event EventHandler<StrokeRenderedEventArgs>? OnStrokeRendered;

    /// <summary>
    /// 스트로크 디스패치 이벤트 (학생 전송용)
    /// </summary>
    event EventHandler<StrokeDispatchedEventArgs>? OnStrokeDispatched;

    /// <summary>
    /// 엔진 활성화
    /// </summary>
    Task ActivateEngineAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 엔진 비활성화
    /// </summary>
    Task DeactivateEngineAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 스트로크 수신 및 처리
    /// </summary>
    Task ReceiveStrokeAsync(AnnotationStroke stroke, CancellationToken cancellationToken = default);

    /// <summary>
    /// 렌더링 파이프라인에 핸들러 추가
    /// </summary>
    void AddRenderHandler(Func<AnnotationStroke, Task> handler);

    /// <summary>
    /// 전송 파이프라인에 핸들러 추가
    /// </summary>
    void AddTransmissionHandler(Func<AnnotationStroke, string, Task> handler);

    /// <summary>
    /// 실제 렌더러 핸들러 추가 (스트로크 그리기용)
    /// </summary>
    void AddRendererHandler(Func<AnnotationStroke, Point[], Task> rendererHandler);

    /// <summary>
    /// 렌더링 파이프라인 초기화
    /// </summary>
    void ClearRenderPipeline();

    /// <summary>
    /// 전송 파이프라인 초기화
    /// </summary>
    void ClearTransmissionPipeline();

    /// <summary>
    /// 렌더러 핸들러 초기화
    /// </summary>
    void ClearRendererHandlers();

    /// <summary>
    /// 판서 레이어 표시/숨김
    /// </summary>
    Task SetLayerVisibilityAsync(bool isVisible, CancellationToken cancellationToken = default);

    /// <summary>
    /// 판서 레이어 표시 토글
    /// </summary>
    Task ToggleLayerVisibilityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 모든 판서 스트로크 삭제
    /// </summary>
    Task ClearAllStrokesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 실행 취소
    /// </summary>
    Task UndoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 특정 참가자의 판서 스트로크 조회
    /// </summary>
    Task<IReadOnlyList<AnnotationStroke>> GetStrokesByParticipantAsync(string participantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 모든 판서 스트로크 조회
    /// </summary>
    Task<IReadOnlyList<AnnotationStroke>> GetAllStrokesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 판서 상태 (Core와 분리된 3번 전용)
/// </summary>
public sealed record AnnotationState
{
    public bool IsDrawing { get; init; }
    public bool IsVisible { get; init; }
    public long ContentRevision { get; init; }
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.UtcNow;

    public static AnnotationState Empty { get; } = new AnnotationState { IsDrawing = false, IsVisible = true, ContentRevision = 0 };
    public AnnotationState SetDrawing(bool drawing) => this with { IsDrawing = drawing, LastUpdated = DateTimeOffset.UtcNow };
    public AnnotationState ToggleVisibility() => this with { IsVisible = !IsVisible, LastUpdated = DateTimeOffset.UtcNow };
    public AnnotationState ContentChanged() => this with { ContentRevision = checked(ContentRevision + 1), LastUpdated = DateTimeOffset.UtcNow };
    public AnnotationState ClearAndStop() => ContentChanged() with { IsDrawing = false, LastUpdated = DateTimeOffset.UtcNow };
}
