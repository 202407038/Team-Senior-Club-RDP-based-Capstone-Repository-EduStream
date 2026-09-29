namespace EduStream.Core.Models;

/// <summary>강의 세션과 별도로 추적하는 학생 한 명의 RDP 연결 상태입니다.</summary>
public enum RdpConnectionState
{
    Idle, Connecting, Connected, Reconnecting, Failed, Closed
}

/// <summary>RDP 어댑터가 공통 계층으로 변환할 실패 원인입니다.</summary>
public enum RdpFailureReason
{
    None, NetworkInterrupted, AuthenticationFailed, AccessDenied,
    UnsupportedEnvironment, HostUnavailable, SessionClosed, Unknown
}
