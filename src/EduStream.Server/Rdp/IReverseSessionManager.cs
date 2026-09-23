using System;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 학생→교수자 역방향 WDS 세션 관리자 인터페이스
/// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
/// </summary>
public interface IReverseSessionManager
{
    /// <summary>
    /// 학생(호스트)로서 역방향 공유 세션 시작
    /// </summary>
    /// <param name="sessionId">강의 세션 ID</param>
    /// <param name="studentId">학생 ID</param>
    /// <param name="cancellationToken">취소 토큰</param>
    /// <returns>공유 ID</returns>
    Task<Guid> StartReverseSharingAsync(Guid sessionId, string studentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 교수자(수신자)를 위한 초대 생성
    /// </summary>
    /// <param name="sessionId">강의 세션 ID</param>
    /// <param name="sharingId">공유 ID</param>
    /// <param name="professorId">교수자 ID</param>
    /// <param name="connectionId">연결 ID</param>
    /// <param name="invitationPassword">초대 비밀번호</param>
    /// <param name="expiresAt">만료 시간</param>
    /// <param name="cancellationToken">취소 토큰</param>
    /// <returns>역방향 초대 패킷</returns>
    Task<ReverseInvitationPacket> CreateProfessorInvitationAsync(
        Guid sessionId,
        Guid sharingId,
        string professorId,
        Guid connectionId,
        string invitationPassword,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 교수자 연결 요청 처리 (Connecting 상태로 전이)
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 연결 성공 처리 (Connected 상태로 전이)
    /// </summary>
    Task OnConnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 연결 실패 처리 (Failed 상태로 전이)
    /// </summary>
    Task OnConnectionFailedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 연결 종료 처리 (Disconnected 상태로 전이)
    /// </summary>
    Task OnDisconnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 프레임 수신 처리
    /// </summary>
    /// <param name="frameData">프레임 데이터</param>
    void ReceiveFrame(byte[] frameData);

    /// <summary>
    /// 역방향 공유 세션 중지
    /// </summary>
    /// <param name="cancellationToken">취소 토큰</param>
    Task StopReverseSharingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 역방향 공유 활성 상태 확인
    /// </summary>
    bool IsReverseSharingActive { get; }

    /// <summary>
    /// 현재 세션 상태
    /// </summary>
    ReverseSessionState CurrentState { get; }

    /// <summary>
    /// 프레임 수신 이벤트
    /// </summary>
    event EventHandler<FrameReceivedEventArgs>? FrameReceived;
}

/// <summary>
/// 프레임 수신 이벤트 인자
/// </summary>
public sealed class FrameReceivedEventArgs : EventArgs
{
    public byte[] FrameData { get; init; } = Array.Empty<byte>();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 역방향 초대 패킷
/// </summary>
public sealed class ReverseInvitationPacket
{
    public Guid SessionId { get; init; }
    public Guid SharingId { get; init; }
    public string ProfessorId { get; init; } = string.Empty;
    public Guid ConnectionId { get; init; }
    public string ConnectionString { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
    public string HostStudentId { get; init; } = string.Empty;
}

/// <summary>
/// 역방향 세션 상태
/// </summary>
public enum ReverseSessionState
{
    /// <summary>
    /// 비활성
    /// </summary>
    Inactive,

    /// <summary>
    /// 호스팅 중 (학생이 화면 공유 중)
    /// </summary>
    Hosting,

    /// <summary>
    /// 연결 중
    /// </summary>
    Connecting,

    /// <summary>
    /// 연결됨 (교수자가 수신 중)
    /// </summary>
    Connected,

    /// <summary>
    /// 연결 종료됨
    /// </summary>
    Disconnected,

    /// <summary>
    /// 연결 실패
    /// </summary>
    Failed,

    /// <summary>
    /// 제어 권한 부여됨
    /// </summary>
    ControlGranted
}
