using System.Text;
using EduStream.Core.Models;
namespace EduStream.Core.Utils;

/// <summary>형식 및 현재 요청 일치 검증. 참가 승인·초대 폐기 여부는 서버 책임입니다.</summary>
public static class RdpInvitationContract
{
    public const int MaximumConnectionStringBytes = 64 * 1024;
    public static readonly TimeSpan InvitationAcceptanceLifetime = TimeSpan.FromMinutes(5);

    public static bool AppliesTo(RdpInvitationRevokedPacket revoked, RdpInvitationPacket active)
    {
        ArgumentNullException.ThrowIfNull(revoked);
        ArgumentNullException.ThrowIfNull(active);
        return revoked.MessageType == PacketType.RdpInvitationRevoked &&
            revoked.DataLength == 0 && active.InvitationId != Guid.Empty &&
            revoked.SessionId == active.SessionId &&
            revoked.ParticipantId == active.ParticipantId &&
            revoked.InvitationId == active.InvitationId &&
            revoked.ConnectionId == active.ConnectionId &&
            Enum.IsDefined(revoked.Reason) && revoked.Reason != RdpFailureReason.None;
    }
    public static void ValidateRequest(RdpInvitationRequestPacket request,
        Guid activeSessionId, string authenticatedParticipantId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MessageType != PacketType.RdpInvitationRequest ||
            request.ContractVersion != 1 || request.DataLength != 0 ||
            activeSessionId == Guid.Empty || request.SessionId != activeSessionId ||
            request.ConnectionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(authenticatedParticipantId) ||
            request.ParticipantId != authenticatedParticipantId ||
            request.SenderId != authenticatedParticipantId)
            throw new ArgumentException("승인된 연결과 초대 요청이 일치하지 않습니다.");
    }

    public static void Validate(RdpInvitationPacket packet, Guid sessionId, string participantId,
        Guid connectionId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.MessageType != PacketType.RdpInvitation ||
            packet.ContractVersion != 1 || packet.Provider != "windows-desktop-sharing")
            throw new ArgumentException("지원하지 않는 RDP 초대 계약입니다.");
        if (sessionId == Guid.Empty || connectionId == Guid.Empty ||
            packet.SessionId != sessionId || packet.ConnectionId != connectionId ||
            string.IsNullOrWhiteSpace(participantId) ||
            !string.Equals(packet.ParticipantId, participantId, StringComparison.Ordinal))
            throw new ArgumentException("현재 세션/참가자/연결 시도와 일치하지 않습니다.");
        if (packet.SharingId == Guid.Empty || packet.InvitationId == Guid.Empty)
            throw new ArgumentException("공유 및 초대 ID가 필요합니다.");
        if (packet.ExpiresAt <= now)
            throw new ArgumentException("만료된 초대입니다.");
        if (!packet.ViewOnly)
            throw new ArgumentException("현재 계약은 보기 전용입니다.");
        if (string.IsNullOrWhiteSpace(packet.ConnectionString))
            throw new ArgumentException("연결 문자열이 필요합니다.");
        var length = Encoding.UTF8.GetByteCount(packet.ConnectionString);
        if (length > MaximumConnectionStringBytes || packet.DataLength != length)
            throw new ArgumentException("연결 문자열 길이가 유효하지 않습니다.");
    }
}
