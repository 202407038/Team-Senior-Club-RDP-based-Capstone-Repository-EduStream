using EduStream.Core.Models;

namespace EduStream.FileTransfer.Tests;

public sealed class AugustWeek1StateTransitionTests
{
    [Theory]
    [InlineData(OperationState.Idle, OperationState.Pending)]
    [InlineData(OperationState.Idle, OperationState.Succeeded)]
    [InlineData(OperationState.Pending, OperationState.InProgress)]
    [InlineData(OperationState.Pending, OperationState.Failed)]
    [InlineData(OperationState.InProgress, OperationState.Succeeded)]
    [InlineData(OperationState.InProgress, OperationState.Stopped)]
    public void CanTransition_ShouldAllowNormalLifecycle(
        OperationState currentState,
        OperationState nextState)
    {
        Assert.True(OperationStateTransitionPolicy.CanTransition(currentState, nextState));
    }

    [Theory]
    [InlineData(OperationState.Succeeded, OperationState.InProgress)]
    [InlineData(OperationState.Stopped, OperationState.InProgress)]
    [InlineData(OperationState.Unknown, OperationState.Pending)]
    [InlineData(OperationState.Pending, OperationState.Idle)]
    public void CanTransition_ShouldRejectInvalidLifecycle(
        OperationState currentState,
        OperationState nextState)
    {
        Assert.False(OperationStateTransitionPolicy.CanTransition(currentState, nextState));
    }

    [Fact]
    public void FailedState_ShouldOnlyRestartWhenRetryIsAllowed()
    {
        Assert.False(OperationStateTransitionPolicy.CanTransition(
            OperationState.Failed,
            OperationState.Pending));
        Assert.True(OperationStateTransitionPolicy.CanTransition(
            OperationState.Failed,
            OperationState.Pending,
            allowRetry: true));
    }
}
