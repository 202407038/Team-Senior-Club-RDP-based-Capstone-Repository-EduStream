namespace EduStream.Core.Models;

/// <summary>
/// 기능과 관계없이 같은 작업 안에서 허용되는 공통 상태 전이를 정의합니다.
/// </summary>
public static class OperationStateTransitionPolicy
{
    public static bool CanTransition(
        OperationState currentState,
        OperationState nextState,
        bool allowRetry = false)
    {
        if (!IsKnownState(currentState) || !IsKnownState(nextState))
        {
            return false;
        }

        if (currentState == nextState)
        {
            return true;
        }

        return currentState switch
        {
            OperationState.Idle => nextState is OperationState.Pending
                or OperationState.InProgress
                or OperationState.Succeeded
                or OperationState.Failed
                or OperationState.Stopped,
            OperationState.Pending => nextState is OperationState.InProgress
                or OperationState.Succeeded
                or OperationState.Failed
                or OperationState.Stopped,
            OperationState.InProgress => nextState is OperationState.Succeeded
                or OperationState.Failed
                or OperationState.Stopped,
            OperationState.Failed => allowRetry && nextState is OperationState.Pending or OperationState.InProgress,
            OperationState.Succeeded or OperationState.Stopped => false,
            _ => false
        };
    }

    public static void EnsureTransition(
        OperationState currentState,
        OperationState nextState,
        bool allowRetry = false)
    {
        if (!CanTransition(currentState, nextState, allowRetry))
        {
            throw new InvalidOperationException($"허용되지 않는 상태 전이입니다: {currentState} -> {nextState}");
        }
    }

    private static bool IsKnownState(OperationState state)
    {
        return state != OperationState.Unknown && Enum.IsDefined(state);
    }
}
