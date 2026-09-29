namespace EduStream.Core.Models;

/// <summary>학생은 일치하는 초대만 해제합니다. 실제 접속 차단은 공유 측에서도 수행합니다.</summary>
public sealed class RdpInvitationRevokedPacket : BasePacket
{
    public RdpInvitationRevokedPacket() => MessageType = PacketType.RdpInvitationRevoked;
    public Guid InvitationId { get; init; }
    public Guid ConnectionId { get; init; }
    public string ParticipantId { get; init; } = string.Empty;
    public RdpFailureReason Reason { get; init; } = RdpFailureReason.SessionClosed;
}
