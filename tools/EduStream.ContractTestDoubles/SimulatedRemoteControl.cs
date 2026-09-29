using EduStream.Core.Collaboration;

namespace EduStream.ContractTestDoubles;

/// <summary>입력 주입 없는 대역. Request 반환으로 Active를 표시하지 않으며 테스트가 별도 승인해야 합니다.</summary>
public sealed class SimulatedRemoteControl(ParticipantConnection professor,
    Func<ParticipantConnection, ParticipantSnapshot> resolve) : IRemoteControlCoordinator
{
    public bool IsSimulation => true;
    public RemoteControlState? Current { get; private set; }
    public event Action<RemoteControlState>? StateChanged;

    public async Task RequestAsync(ParticipantConnection target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var next = RemoteControlState.Request(professor, resolve(target), Guid.NewGuid());
        if (next.Student != target) throw new CollaborationException(CollaborationError.StaleConnection);
        await StopAsync(cancellationToken);
        Current = next;
        StateChanged?.Invoke(Current);
    }

    public void ConfirmNativeControlForTest()
    {
        var current = Current ?? throw new CollaborationException(CollaborationError.InvalidRequest);
        Current = current.Activate(current.RequestId, resolve(current.Student));
        StateChanged?.Invoke(Current);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Current is not null && Current.Phase != ControlPhase.Revoked)
        {
            Current = Current.Revoke();
            StateChanged?.Invoke(Current);
        }
        return Task.CompletedTask;
    }
}
