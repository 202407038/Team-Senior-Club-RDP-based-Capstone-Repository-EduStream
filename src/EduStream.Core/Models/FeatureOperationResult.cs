using EduStream.Core.Utils;

namespace EduStream.Core.Models;

/// <summary>
/// 기능별 상태와 ACK/Error 응답을 하나의 통합 상태 형태로 다루기 위한 공통 모델입니다.
/// </summary>
public sealed class FeatureOperationResult
{
    public FeatureArea Feature { get; init; }

    public OperationState State { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool IsRecoverable { get; init; }

    public int? ProgressPercent { get; init; }

    public Guid? SessionId { get; init; }

    public Guid CorrelationId { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public bool IsSuccess => State == OperationState.Succeeded;

    public bool IsPending => State is OperationState.Pending or OperationState.InProgress;

    public bool IsTerminal => State is OperationState.Succeeded or OperationState.Failed or OperationState.Stopped;

    public static FeatureOperationResult FromAck(FeatureArea feature, AckPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ValidateFeature(feature);
        PacketContractUtility.ValidateAckCode(packet.AckCode);

        return new FeatureOperationResult
        {
            Feature = feature,
            State = OperationState.Succeeded,
            Code = packet.AckCode,
            Message = packet.Message,
            SessionId = packet.SessionId,
            CorrelationId = packet.CorrelationId,
            OccurredAt = packet.CreatedAt
        };
    }

    public static FeatureOperationResult FromError(FeatureArea feature, ErrorPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ValidateFeature(feature);
        PacketContractUtility.ValidateErrorCode(packet.ErrorCode);

        return new FeatureOperationResult
        {
            Feature = feature,
            State = OperationState.Failed,
            Code = packet.ErrorCode,
            Message = packet.Message,
            IsRecoverable = packet.IsRecoverable,
            SessionId = packet.SessionId,
            CorrelationId = packet.CorrelationId,
            OccurredAt = packet.CreatedAt
        };
    }

    public static FeatureOperationResult CreateStatus(
        FeatureArea feature,
        OperationState state,
        string message,
        int? progressPercent = null,
        Guid? sessionId = null,
        Guid? correlationId = null,
        DateTimeOffset? occurredAt = null,
        string? code = null,
        bool isRecoverable = false)
    {
        ValidateFeature(feature);
        ValidateState(state);

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("상태 메시지는 비워둘 수 없습니다.", nameof(message));
        }

        if (progressPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(progressPercent), "진행률은 0에서 100 사이여야 합니다.");
        }

        return new FeatureOperationResult
        {
            Feature = feature,
            State = state,
            Code = code ?? string.Empty,
            Message = message,
            IsRecoverable = isRecoverable,
            ProgressPercent = progressPercent,
            SessionId = sessionId,
            CorrelationId = correlationId ?? Guid.NewGuid(),
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow
        };
    }

    private static void ValidateFeature(FeatureArea feature)
    {
        if (feature == FeatureArea.Unknown || !Enum.IsDefined(feature))
        {
            throw new ArgumentOutOfRangeException(nameof(feature), "알 수 없는 기능 영역입니다.");
        }
    }

    private static void ValidateState(OperationState state)
    {
        if (state == OperationState.Unknown || !Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), "알 수 없는 작업 상태입니다.");
        }
    }
}
