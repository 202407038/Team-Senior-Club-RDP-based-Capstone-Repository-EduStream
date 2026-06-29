using System.Reflection;
using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.Core.Utils;

/// <summary>
/// 공통 패킷 계약에서 서버와 클라이언트가 함께 확인해야 하는 최소 검증 규칙입니다.
/// </summary>
public static class PacketContractUtility
{
    private static readonly Lazy<IReadOnlySet<string>> KnownAckCodes = new(() => GetPublicStringConstants(typeof(AckCodes)));
    private static readonly Lazy<IReadOnlySet<string>> KnownErrorCodes = new(() => GetPublicStringConstants(typeof(ErrorCodes)));

    public static bool IsKnownPacketType(PacketType packetType)
    {
        return packetType != PacketType.Unknown && Enum.IsDefined(packetType);
    }

    public static bool IsKnownAckCode(string? ackCode)
    {
        return !string.IsNullOrWhiteSpace(ackCode) && KnownAckCodes.Value.Contains(ackCode);
    }

    public static bool IsKnownErrorCode(string? errorCode)
    {
        return !string.IsNullOrWhiteSpace(errorCode) && KnownErrorCodes.Value.Contains(errorCode);
    }

    public static void ValidatePacketType(PacketType packetType)
    {
        if (!IsKnownPacketType(packetType))
        {
            throw new InvalidOperationException(ErrorCodes.InvalidPacketType);
        }
    }

    public static void ValidateAckCode(string? ackCode)
    {
        if (!IsKnownAckCode(ackCode))
        {
            throw new InvalidOperationException(ErrorCodes.InvalidAckCode);
        }
    }

    public static void ValidateErrorCode(string? errorCode)
    {
        if (!IsKnownErrorCode(errorCode))
        {
            throw new InvalidOperationException(ErrorCodes.InvalidErrorCode);
        }
    }

    public static void ValidatePayloadLength(int declaredLength, int actualLength)
    {
        if (declaredLength != actualLength)
        {
            throw new InvalidOperationException(ErrorCodes.PayloadLengthMismatch);
        }
    }

    private static IReadOnlySet<string> GetPublicStringConstants(Type type)
    {
        return type
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
