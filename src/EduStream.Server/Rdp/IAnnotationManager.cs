using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 판서(Annotation) 관리자 인터페이스
/// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
/// 화면 공유 스트림 위에 투명 레이어로 판서 좌표를 렌더링하고 동기화
/// </summary>
public interface IAnnotationManager
{
    /// <summary>
    /// 새 판서 스트로크 추가
    /// </summary>
    /// <param name="stroke">판서 스트로크</param>
    /// <param name="cancellationToken">취소 토큰</param>
    Task AddStrokeAsync(AnnotationStroke stroke, CancellationToken cancellationToken = default);

    /// <summary>
    /// 기존 판서 스트로크 수정
    /// </summary>
    /// <param name="strokeId">스트로크 ID</param>
    /// <param name="updatedStroke">수정된 스트로크</param>
    /// <param name="cancellationToken">취소 토큰</param>
    Task UpdateStrokeAsync(Guid strokeId, AnnotationStroke updatedStroke, CancellationToken cancellationToken = default);

    /// <summary>
    /// 판서 스트로크 삭제
    /// </summary>
    /// <param name="strokeId">스트로크 ID</param>
    /// <param name="cancellationToken">취소 토큰</param>
    Task DeleteStrokeAsync(Guid strokeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 모든 판서 스트로크 삭제
    /// </summary>
    /// <param name="cancellationToken">취소 토큰</param>
    Task ClearAllStrokesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 특정 참가자의 판서 스트로크 조회
    /// </summary>
    /// <param name="participantId">참가자 ID</param>
    /// <param name="cancellationToken">취소 토큰</param>
    Task<IReadOnlyList<AnnotationStroke>> GetStrokesByParticipantAsync(string participantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 모든 판서 스트로크 조회
    /// </summary>
    /// <param name="cancellationToken">취소 토큰</param>
    Task<IReadOnlyList<AnnotationStroke>> GetAllStrokesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 판서 레이어 표시/숨김 토글
    /// </summary>
    /// <param name="isVisible">표시 여부</param>
    /// <param name="cancellationToken">취소 토큰</param>
    Task SetLayerVisibilityAsync(bool isVisible, CancellationToken cancellationToken = default);

    /// <summary>
    /// 판서 레이어 표시 상태
    /// </summary>
    bool IsLayerVisible { get; }
}

/// <summary>
/// 판서 스트로크
/// </summary>
public sealed class AnnotationStroke
{
    public Guid StrokeId { get; init; } = Guid.NewGuid();
    public string ParticipantId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public AnnotationTool Tool { get; init; }
    public AnnotationColor Color { get; init; } = AnnotationColor.Black;
    public int StrokeWidth { get; init; } = 2;
    public IReadOnlyList<Point> Points { get; init; } = Array.Empty<Point>();
    public bool IsVisible { get; init; } = true;
}

/// <summary>
/// 판서 도구
/// </summary>
public enum AnnotationTool
{
    /// <summary>
    /// 펜 (자유 곡선)
    /// </summary>
    Pen,

    /// <summary>
    /// 형광펜 (반투명)
    /// </summary>
    Highlighter,

    /// <summary>
    /// 지우개
    /// </summary>
    Eraser,

    /// <summary>
    /// 직선
    /// </summary>
    Line,

    /// <summary>
    /// 사각형
    /// </summary>
    Rectangle,

    /// <summary>
    /// 원
    /// </summary>
    Circle,

    /// <summary>
    /// 화살표
    /// </summary>
    Arrow,

    /// <summary>
    /// 텍스트
    /// </summary>
    Text
}

/// <summary>
/// 판서 색상
/// </summary>
public readonly record struct AnnotationColor
{
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
    public byte A { get; init; }

    public AnnotationColor(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public static AnnotationColor Red => new(255, 0, 0);
    public static AnnotationColor Green => new(0, 255, 0);
    public static AnnotationColor Blue => new(0, 0, 255);
    public static AnnotationColor Black => new(0, 0, 0);
    public static AnnotationColor White => new(255, 255, 255);
    public static AnnotationColor Yellow => new(255, 255, 0);

    public string ToHex() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// 판서 레이어 정보
/// </summary>
public sealed class AnnotationLayer
{
    public Guid LayerId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public int ZIndex { get; init; } = 0;
    public bool IsVisible { get; init; } = true;
    public double Opacity { get; init; } = 1.0;
}
