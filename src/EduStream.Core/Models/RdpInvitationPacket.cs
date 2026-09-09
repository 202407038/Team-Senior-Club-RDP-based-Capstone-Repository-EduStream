namespace EduStream.Core.Models;

/// <summary>참가 승인된 학생에게 개별 전달하는 WDS 초대. 비밀번호는 별도 경로로 전달합니다.</summary>
public sealed class RdpInvitationPacket : BasePacket
{
    public RdpInvitationPacket() => MessageType = PacketType.RdpInvitation;
    public int ContractVersion { get; init; } = 1;
    public string Provider { get; init; } = "windows-desktop-sharing";
    public Guid SharingId { get; init; }
    public Guid InvitationId { get; init; }
    public Guid ConnectionId { get; init; }
    public string ParticipantId { get; init; } = string.Empty;
    public string ConnectionString { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
    public bool ViewOnly { get; init; } = true;
    public override string ToString() =>
        $"RDP invitation: session={SessionId}, sharing={SharingId}, invitation={InvitationId}, connection={ConnectionId} [REDACTED]";
}
