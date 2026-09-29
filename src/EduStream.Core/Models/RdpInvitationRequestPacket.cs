namespace EduStream.Core.Models;

/// <summary>학생이 새 연결 ID로 초대를 요청합니다. 서버는 연결의 승인된 참가자와 대조합니다.</summary>
public sealed class RdpInvitationRequestPacket : BasePacket
{
    public RdpInvitationRequestPacket() => MessageType = PacketType.RdpInvitationRequest;
    public int ContractVersion { get; init; } = 1;
    public Guid ConnectionId { get; init; }
    public string ParticipantId { get; init; } = string.Empty;
}
