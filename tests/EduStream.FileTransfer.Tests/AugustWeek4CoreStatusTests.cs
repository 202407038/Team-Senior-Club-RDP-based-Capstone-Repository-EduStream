using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.FileTransfer.Tests;

public sealed class AugustWeek4StatusRegressionTests
{
    [Fact]
    public void RecoverableFailure_ShouldSupportRetryButNotSuccessJump()
    {
        var failure = FeatureOperationResult.CreateStatus(
            FeatureArea.File,
            OperationState.Failed,
            "파일 검증 실패",
            code: ErrorCodes.ChecksumMismatch,
            isRecoverable: true);

        Assert.True(failure.CanRetry);
        Assert.True(failure.CanTransitionTo(OperationState.InProgress));
        Assert.False(failure.CanTransitionTo(OperationState.Succeeded));
    }

    [Fact]
    public void NonRecoverableFailure_ShouldRemainTerminal()
    {
        var failure = FeatureOperationResult.CreateStatus(
            FeatureArea.Protocol,
            OperationState.Failed,
            "지원하지 않는 패킷",
            code: ErrorCodes.InvalidPacketType,
            isRecoverable: false);

        Assert.False(failure.CanRetry);
        Assert.False(failure.CanTransitionTo(OperationState.Pending));
        Assert.True(failure.IsTerminal);
    }
}
