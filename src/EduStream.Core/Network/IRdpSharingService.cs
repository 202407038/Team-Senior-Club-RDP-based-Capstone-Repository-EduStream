using EduStream.Core.Models;
namespace EduStream.Core.Network;

/// <summary>3번 구현. 내부에서 STA/UI 스레드 호출과 COM 수명을 관리합니다.</summary>
public interface IRdpSharingService : IAsyncDisposable
{
    // 동일 교수자 데스크톱에 공유 객체 하나. 시작 시 새 SharingId 반환.
    Task<Guid> StartAsync(Guid sessionId, CancellationToken cancellationToken = default);
    // 2번이 승인한 참가자만 호출. 초대마다 limit=1, password는 별도 전달용.
    Task<RdpInvitationPacket> CreateInvitationAsync(Guid sessionId, Guid sharingId,
        string participantId, Guid connectionId, string invitationPassword,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    // 초대 폐기와 해당 활성 attendee 연결 해제 모두 수행.
    Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default);
    // 전체 초대 폐기, attendee 해제, 공유 객체 종료. 반복 호출 허용.
    Task StopAsync(CancellationToken cancellationToken = default);
}
