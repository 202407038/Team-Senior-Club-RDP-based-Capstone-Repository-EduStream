using System.Reflection;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Utils;

namespace EduStream.FileTransfer.Tests;

public sealed class ProtocolCodeTests
{
    [Fact]
    public void ErrorCodes_ShouldBeUniqueAndNonEmpty()
    {
        var values = GetPublicStringConstants(typeof(ErrorCodes));

        Assert.All(values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AckCodes_ShouldBeUniqueAndNonEmpty()
    {
        var values = GetPublicStringConstants(typeof(AckCodes));

        Assert.All(values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(PacketType.SessionJoin)]
    [InlineData(PacketType.SessionLeave)]
    [InlineData(PacketType.Chat)]
    [InlineData(PacketType.File)]
    [InlineData(PacketType.Screen)]
    [InlineData(PacketType.Ack)]
    [InlineData(PacketType.Error)]
    [InlineData(PacketType.Heartbeat)]
    public void PacketContractUtility_ShouldAcceptKnownPacketTypes(PacketType packetType)
    {
        PacketContractUtility.ValidatePacketType(packetType);

        Assert.True(PacketContractUtility.IsKnownPacketType(packetType));
    }

    [Theory]
    [InlineData(PacketType.Unknown)]
    [InlineData((PacketType)999)]
    public void PacketContractUtility_ShouldRejectUnknownPacketTypes(PacketType packetType)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PacketContractUtility.ValidatePacketType(packetType));

        Assert.Equal(ErrorCodes.InvalidPacketType, ex.Message);
        Assert.False(PacketContractUtility.IsKnownPacketType(packetType));
    }

    [Fact]
    public void PacketContractUtility_ShouldValidateAckAndErrorCodes()
    {
        PacketContractUtility.ValidateAckCode(AckCodes.SessionJoined);
        PacketContractUtility.ValidateErrorCode(ErrorCodes.ChecksumMismatch);

        Assert.True(PacketContractUtility.IsKnownAckCode(AckCodes.FileAccepted));
        Assert.True(PacketContractUtility.IsKnownErrorCode(ErrorCodes.PayloadLengthMismatch));
    }

    [Theory]
    [InlineData("")]
    [InlineData("UNKNOWN_ACK")]
    public void PacketContractUtility_ShouldRejectInvalidAckCodes(string ackCode)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PacketContractUtility.ValidateAckCode(ackCode));

        Assert.Equal(ErrorCodes.InvalidAckCode, ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UNKNOWN_ERROR")]
    public void PacketContractUtility_ShouldRejectInvalidErrorCodes(string errorCode)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PacketContractUtility.ValidateErrorCode(errorCode));

        Assert.Equal(ErrorCodes.InvalidErrorCode, ex.Message);
    }

    [Fact]
    public void PacketContractUtility_ShouldRejectPayloadLengthMismatch()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PacketContractUtility.ValidatePayloadLength(10, 9));

        Assert.Equal(ErrorCodes.PayloadLengthMismatch, ex.Message);
    }

    private static IReadOnlyList<string> GetPublicStringConstants(Type type)
    {
        return type
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();
    }
}
