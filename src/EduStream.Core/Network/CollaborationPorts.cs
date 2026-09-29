namespace EduStream.Core.Network;

/// <summary>
/// 보호 채널 포트 규칙. 학생이 입력한 세션 포트 하나로 두 연결을 모두 찾도록 세션 포트 + 1을 씁니다.
/// </summary>
public static class CollaborationPorts
{
    public const int Offset = 1;

    public static int ForSession(int sessionPort)
    {
        if (sessionPort is < 1 or > 65535 - Offset)
            throw new ArgumentOutOfRangeException(nameof(sessionPort), "보호 채널 포트를 만들 수 없는 세션 포트입니다.");
        return sessionPort + Offset;
    }
}
