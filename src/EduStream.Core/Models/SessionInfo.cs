using EduStream.Core.Network;

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

    /// <summary>
    /// UI나 로그에서 바로 사용할 수 있는 접속 문자열입니다.
    /// </summary>
    public string DisplayAddress => $"{HostAddress}:{Port}";

    public SessionEndpoint ToEndpoint()
    {
        return new SessionEndpoint
        {
            Address = HostAddress,
            Port = Port
        };
    }
}
