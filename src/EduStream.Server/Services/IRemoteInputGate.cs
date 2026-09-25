using EduStream.Core.Collaboration;

namespace EduStream.Server.Services;

/// <summary>
/// 2번 승인/회수 흐름과 3번 실제 입력 엔진 사이의 연동 접점입니다.
/// ServerRemoteControlCoordinator는 이 결과를 기다린 뒤에만 Active 표시·대상 전환을 진행합니다.
/// </summary>
/// <remarks>
/// 현재 단계에서는 서버 프로젝트 내부 접점입니다. 3번 구현 위치(교수자 viewer/학생 sharer)와
/// 학생 측 차단 메시지 계약이 정해지면 Core 이동 여부를 협의합니다.
/// </remarks>
public interface IRemoteInputGate
{
    /// <summary>
    /// 요청된 대상에게 실제 입력을 허용합니다. native 허용이 끝났을 때만 정상 완료해야 하며,
    /// 허용하지 못했으면 예외를 던집니다. 회수로 요청이 대체되면 cancellationToken이 취소됩니다.
    /// </summary>
    Task GrantAsync(RemoteControlState requested, CancellationToken cancellationToken);

    /// <summary>
    /// 해당 요청의 실제 입력을 차단합니다. native 차단을 확인한 뒤에만 정상 완료해야 하며,
    /// 허용 전이거나 이미 차단된 요청에 대해서도 성공해야 합니다(멱등).
    /// </summary>
    Task RevokeAsync(RemoteControlState revoked, CancellationToken cancellationToken);
}

/// <summary>
/// 3번 입력 엔진이 연결되기 전 기본값입니다. 허용은 항상 실패하므로 제어가 Active로 표시되지 않고,
/// 허용된 입력이 없으므로 회수는 즉시 성공합니다.
/// </summary>
public sealed class UnavailableRemoteInputGate : IRemoteInputGate
{
    public static UnavailableRemoteInputGate Instance { get; } = new();

    private UnavailableRemoteInputGate() { }

    public Task GrantAsync(RemoteControlState requested, CancellationToken cancellationToken) =>
        Task.FromException(new CollaborationException(CollaborationError.UnsupportedCapability));

    public Task RevokeAsync(RemoteControlState revoked, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
