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
    /// 레이어 상태 동기화 완료 이벤트.
    /// 숨김/재표시/전체 삭제/실행 취소 결과가 등록된 싱크(렌더러·전송) 중 하나 이상에 실제로 적용됐을 때만 발생한다.
    /// </summary>
    event EventHandler<AnnotationLayerSyncedEventArgs>? OnLayerSynced;

    /// <summary>
    /// 레이어 동기화 싱크 등록.
    /// 숨김/재표시/전체 삭제/실행 취소가 일어날 때마다 "지금 화면에 있어야 할 전체 스트로크" 스냅샷이 전달된다.
    /// 싱크는 자신의 출력(로컬 캔버스, 학생 화면 오버레이 등)을 이 스냅샷으로 통째로 교체해야 한다.
    /// </summary>
    void AddLayerSyncHandler(Func<AnnotationLayerSnapshot, Task> handler);

    /// <summary>
    /// 레이어 동기화 싱크 초기화
    /// </summary>
    void ClearLayerSyncHandlers();

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

/// <summary>레이어 동기화를 일으킨 변경 종류</summary>
public enum AnnotationLayerChange
{
    VisibilityChanged,
    Cleared,
    Undone,
    Erased
}

/// <summary>
/// 레이어 동기화 스냅샷. 싱크는 자신의 출력을 <see cref="VisibleStrokes"/> 로 통째로 교체한다.
/// 숨김 상태이면 <see cref="VisibleStrokes"/> 는 비어 있다(스트로크 자체는 매니저에 보존된다).
/// </summary>
public sealed class AnnotationLayerSnapshot
{
    public AnnotationLayerChange Change { get; init; }
    public bool IsVisible { get; init; }
    public long ContentRevision { get; init; }
    public IReadOnlyList<AnnotationStroke> VisibleStrokes { get; init; } = Array.Empty<AnnotationStroke>();
}

/// <summary>레이어 동기화 완료 이벤트 인자</summary>
public sealed class AnnotationLayerSyncedEventArgs : EventArgs
{
    public AnnotationLayerSnapshot Snapshot { get; init; } = null!;
    public int AppliedHandlerCount { get; init; }
    public DateTimeOffset SyncedAt { get; init; } = DateTimeOffset.UtcNow;
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
