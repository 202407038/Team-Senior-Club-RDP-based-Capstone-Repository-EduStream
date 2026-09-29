using EduStream.Core.Models;
namespace EduStream.Core.Network;

/// <summary>5번 구현. UI/ActiveX 객체는 Client 내부에서 생성·연결합니다.</summary>
public interface IRdpViewerService : IAsyncDisposable
{
    event Action<RdpConnectionStatus>? StatusChanged;
    // 메서드 반환은 연결 시작. Connected는 실제 COM 성공 이벤트에서만 알림.
    Task ConnectAsync(RdpInvitationPacket invitation, string invitationPassword,
        CancellationToken cancellationToken = default);
    // 연결 시도 취소/연결 해제 및 지연 이벤트 무시. 반복 호출 허용.
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
