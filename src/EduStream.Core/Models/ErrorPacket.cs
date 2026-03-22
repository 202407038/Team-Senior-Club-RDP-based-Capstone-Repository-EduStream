namespace EduStream.Core.Models;

/// <summary>
/// 세션 연결 실패, 체크섬 오류 등 공통 오류를 전달하는 패킷입니다.
/// </summary>
public sealed class ErrorPacket : BasePacket
{
    public ErrorPacket()
    {
        MessageType = PacketType.Error;
    }

    public string ErrorCode { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public bool IsRecoverable { get; set; }
}
