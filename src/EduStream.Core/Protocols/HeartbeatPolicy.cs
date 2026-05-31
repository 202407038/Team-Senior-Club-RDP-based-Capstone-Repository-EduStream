namespace EduStream.Core.Protocols;

/// <summary>
/// 하트비트 송신 주기와 비활성 클라이언트 제거 기준을 한 곳에서 관리합니다.
/// 서버 구현과 테스트가 같은 정책 값을 공유하도록 상수로 노출합니다.
/// </summary>
public static class HeartbeatPolicy
{
    /// <summary>
    /// 서버가 참여자 전원에게 하트비트 패킷을 브로드캐스트하는 주기입니다.
    /// </summary>
    public static readonly TimeSpan SendInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 클라이언트로부터 마지막 패킷을 받은 시점으로부터 이 시간이 지나면
    /// 응답 불가 상태로 간주하여 연결을 끊습니다.
    /// SendInterval의 3배로 잡아 일시적 지연을 허용합니다.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 비활성 클라이언트 검사를 수행하는 주기입니다.
    /// </summary>
    public static readonly TimeSpan StaleCheckInterval = TimeSpan.FromSeconds(5);
}
