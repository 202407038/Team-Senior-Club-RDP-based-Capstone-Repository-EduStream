using System;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 역방향 화면 공유 어댑터 인터페이스
/// 학생 화면 WDS 프레임을 교수자가 수신 처리하는 최소 실행 접점
/// </summary>
public interface IReverseScreenShareAdapter
{
    /// <summary>
    /// 어댑터 활성 상태
    /// </summary>
    bool IsAdapterActive { get; }

    /// <summary>
    /// 현재 세션 상태
    /// </summary>
    ReverseSessionState CurrentState { get; }

    /// <summary>
    /// 어댑터 상태 변경 이벤트
    /// </summary>
    event EventHandler<AdapterStateChangedEventArgs>? AdapterStateChanged;

    /// <summary>
    /// 프레임 처리 이벤트
    /// </summary>
    event EventHandler<FrameProcessedEventArgs>? FrameProcessed;

    /// <summary>
    /// 프레임 디스플레이 이벤트 (실제 화면 표시용)
    /// </summary>
    event EventHandler<FrameDisplayedEventArgs>? FrameDisplayed;

    /// <summary>
    /// 어댑터 활성화
    /// </summary>
    Task ActivateAdapterAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 어댑터 비활성화
    /// </summary>
    Task DeactivateAdapterAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 역방향 공유 세션 시작
    /// </summary>
    Task<Guid> StartReverseSharingAsync(Guid sessionId, string studentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 교수자 초대 생성
    /// </summary>
    Task<ReverseInvitationPacket> CreateProfessorInvitationAsync(
        Guid sessionId,
        Guid sharingId,
        string professorId,
        Guid connectionId,
        string invitationPassword,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 교수자 연결 요청
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 연결 성공 처리
    /// </summary>
    Task OnConnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 연결 실패 처리
    /// </summary>
    Task OnConnectionFailedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 연결 종료 처리
    /// </summary>
    Task OnDisconnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 역방향 공유 세션 중지
    /// </summary>
    Task StopReverseSharingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 프레임 수신 핸들러 추가
    /// </summary>
    void AddFrameReceiver(Func<byte[], Task> receiver);

    /// <summary>
    /// 디스플레이 핸들러 추가 (실제 화면 표시용)
    /// </summary>
    void AddDisplayHandler(Func<byte[], int, int, Task> displayHandler);

    /// <summary>
    /// 프레임 수신 핸들러 초기화
    /// </summary>
    void ClearFrameReceivers();

    /// <summary>
    /// 디스플레이 핸들러 초기화
    /// </summary>
    void ClearDisplayHandlers();
}

/// <summary>
/// 프레임 디스플레이 이벤트 인자 (실제 화면 표시용)
/// </summary>
public sealed class FrameDisplayedEventArgs : EventArgs
{
    public byte[] FrameData { get; init; } = Array.Empty<byte>();
    public int Width { get; init; }
    public int Height { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DisplayedAt { get; init; } = DateTimeOffset.UtcNow;
}
