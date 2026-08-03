using EduStream.Core.Models;

namespace EduStream.FileTransfer.Tests;

public sealed class AugustWeek3StatusContractTests
{
    [Fact]
    public void CompletedStatus_ShouldRejectPartialProgress()
    {
        Assert.Throws<ArgumentException>(() =>
            FeatureOperationResult.CreateStatus(
                FeatureArea.File,
                OperationState.Succeeded,
                "파일 수신 완료",
                progressPercent: 99));
    }

    [Fact]
    public void IdleStatus_ShouldRejectActiveProgress()
    {
        Assert.Throws<ArgumentException>(() =>
            FeatureOperationResult.CreateStatus(
                FeatureArea.Screen,
                OperationState.Idle,
                "화면 공유 대기",
                progressPercent: 1));
    }

    [Fact]
    public void CompletedStatus_ShouldAcceptOneHundredPercent()
    {
        var result = FeatureOperationResult.CreateStatus(
            FeatureArea.File,
            OperationState.Succeeded,
            "파일 수신 완료",
            progressPercent: 100);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsTerminal);
        Assert.Equal(100, result.ProgressPercent);
        Assert.Equal(result.Message, result.DisplayMessage);
    }
}
