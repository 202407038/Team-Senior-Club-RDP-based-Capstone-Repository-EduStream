using System.Reflection;
using EduStream.Core.Factories;
using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.FileTransfer.Tests;

public sealed class AugustWeek2ErrorMappingTests
{
    [Fact]
    public void ErrorCatalog_ShouldCoverEveryCommonErrorCode()
    {
        var errorCodes = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

        foreach (var errorCode in errorCodes)
        {
            Assert.True(FeatureErrorCatalog.TryResolve(errorCode, out var error), errorCode);
            Assert.NotNull(error);
            Assert.False(string.IsNullOrWhiteSpace(error!.UserMessage));
            Assert.NotEqual(FeatureArea.Unknown, error.Feature);
        }
    }

    [Theory]
    [InlineData(ErrorCodes.SessionNotOpen, FeatureArea.Session, false)]
    [InlineData(ErrorCodes.ChecksumMismatch, FeatureArea.File, true)]
    [InlineData(ErrorCodes.InvalidFrameDimensions, FeatureArea.Screen, true)]
    [InlineData(ErrorCodes.MessageTooLong, FeatureArea.Chat, true)]
    [InlineData(ErrorCodes.InvalidPacketType, FeatureArea.Protocol, false)]
    public void Resolve_ShouldMapFeatureAndDefaultRecovery(
        string errorCode,
        FeatureArea expectedFeature,
        bool expectedRecoverable)
    {
        var error = FeatureErrorCatalog.Resolve(errorCode);

        Assert.Equal(expectedFeature, error.Feature);
        Assert.Equal(expectedRecoverable, error.DefaultRecoverable);
    }

    [Fact]
    public void FromError_ShouldKeepDiagnosticMessageAndUseCommonDisplayMessage()
    {
        var packet = PacketFactory.CreateError(
            "Server",
            ErrorCodes.ChecksumMismatch,
            "checksum mismatch at chunk 4",
            isRecoverable: true);

        var result = FeatureOperationResult.FromError(FeatureArea.File, packet);

        Assert.Equal("checksum mismatch at chunk 4", result.Message);
        Assert.Equal(FeatureErrorCatalog.Resolve(ErrorCodes.ChecksumMismatch).UserMessage, result.DisplayMessage);
        Assert.True(result.CanRetry);
        Assert.True(result.CanTransitionTo(OperationState.Pending));
    }
}
