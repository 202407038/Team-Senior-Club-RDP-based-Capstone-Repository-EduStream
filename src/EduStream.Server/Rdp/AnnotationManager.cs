using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 판서(Annotation) 관리자 프로토타입 구현
/// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
/// 화면 공유 스트림 위에 투명 레이어로 판서 좌표를 렌더링하고 동기화
/// </summary>
public sealed class AnnotationManager : IAnnotationManager
{
    private readonly ConcurrentDictionary<Guid, AnnotationStroke> _strokes = new();
    private readonly ConcurrentDictionary<string, List<Guid>> _participantStrokeMap = new();
    private bool _isLayerVisible = true;

    public bool IsLayerVisible => _isLayerVisible;

    public Task AddStrokeAsync(AnnotationStroke stroke, CancellationToken cancellationToken = default)
    {
        _strokes[stroke.StrokeId] = stroke;

        if (!_participantStrokeMap.ContainsKey(stroke.ParticipantId))
        {
            _participantStrokeMap[stroke.ParticipantId] = new List<Guid>();
        }
        _participantStrokeMap[stroke.ParticipantId].Add(stroke.StrokeId);

        return Task.CompletedTask;
    }

    public Task UpdateStrokeAsync(Guid strokeId, AnnotationStroke updatedStroke, CancellationToken cancellationToken = default)
    {
        if (_strokes.ContainsKey(strokeId))
        {
            _strokes[strokeId] = updatedStroke;
        }

        return Task.CompletedTask;
    }

    public Task DeleteStrokeAsync(Guid strokeId, CancellationToken cancellationToken = default)
    {
        if (_strokes.TryRemove(strokeId, out var stroke))
        {
            if (_participantStrokeMap.TryGetValue(stroke.ParticipantId, out var strokeList))
            {
                strokeList.Remove(strokeId);
            }
        }

        return Task.CompletedTask;
    }

    public Task ClearAllStrokesAsync(CancellationToken cancellationToken = default)
    {
        _strokes.Clear();
        _participantStrokeMap.Clear();

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AnnotationStroke>> GetStrokesByParticipantAsync(string participantId, CancellationToken cancellationToken = default)
    {
        if (_participantStrokeMap.TryGetValue(participantId, out var strokeIds))
        {
            var strokes = strokeIds
                .Select(id => _strokes.TryGetValue(id, out var stroke) ? stroke : null)
                .Where(stroke => stroke != null)
                .Select(stroke => stroke!)
                .ToList();

            return Task.FromResult<IReadOnlyList<AnnotationStroke>>(strokes);
        }

        return Task.FromResult<IReadOnlyList<AnnotationStroke>>(Array.Empty<AnnotationStroke>());
    }

    public Task<IReadOnlyList<AnnotationStroke>> GetAllStrokesAsync(CancellationToken cancellationToken = default)
    {
        var strokes = _strokes.Values.ToList();
        return Task.FromResult<IReadOnlyList<AnnotationStroke>>(strokes);
    }

    public Task SetLayerVisibilityAsync(bool isVisible, CancellationToken cancellationToken = default)
    {
        _isLayerVisible = isVisible;
        return Task.CompletedTask;
    }
}
