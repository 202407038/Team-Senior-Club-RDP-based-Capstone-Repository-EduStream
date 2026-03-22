namespace EduStream.Core.Models;

/// <summary>
/// 세션 참여/개설에 필요한 최소 연결 정보를 보관합니다.
/// </summary>
public sealed class SessionInfo
{
    public Guid SessionId { get; set; } = Guid.NewGuid();

    public string SessionName { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public string HostAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5000;

    public int ParticipantCount { get; set; }

    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
}
