namespace EduStream.Core.Models;

/// <summary>전체 채팅과 별도 타입: 미지원 앱이 개인 메시지를 전체 전파하지 못하게 한다.</summary>
public sealed class DirectChatPacket : BasePacket
{
    public DirectChatPacket() => MessageType = PacketType.DirectChat;
    public string Message { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public Guid StudentConnectionId { get; set; }
    public bool FromProfessor { get; set; }
}
