using System;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 원격 제어 입력 레벨 관리자 인터페이스
/// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
/// </summary>
public interface IRemoteControlManager
{
    /// <summary>
    /// 현재 제어 레벨 조회
    /// </summary>
    ControlLevel CurrentControlLevel { get; }

    /// <summary>
    /// 제어 레벨 설정
    /// </summary>
    /// <param name="level">제어 레벨</param>
    /// <param name="cancellationToken">취소 토큰</param>
    Task SetControlLevelAsync(ControlLevel level, CancellationToken cancellationToken = default);

    /// <summary>
    /// 특정 참가자에게 제어 권한 부여
    /// </summary>
    /// <param name="participantId">참가자 ID</param>
    /// <param name="cancellationToken">취소 토큰</param>
    Task GrantControlAsync(string participantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 특정 참가자의 제어 권한 철회
    /// </summary>
    /// <param name="participantId">참가자 ID</param>
    /// <param name="cancellationToken">취소 토큰</param>
    Task RevokeControlAsync(string participantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 모든 제어 권한 철회
    /// </summary>
    /// <param name="cancellationToken">취소 토큰</param>
    Task RevokeAllControlAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 참가자의 현재 제어 권한 상태 조회
    /// </summary>
    /// <param name="participantId">참가자 ID</param>
    /// <returns>제어 권한 상태</returns>
    Task<ControlPermission> GetParticipantPermissionAsync(string participantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 마우스 이동 입력 처리
    /// </summary>
    /// <param name="participantId">참가자 ID</param>
    /// <param name="x">X 좌표</param>
    /// <param name="y">Y 좌표</param>
    /// <returns>입력 처리 여부 (true=처리됨, false=차단됨)</returns>
    bool ProcessMouseMove(string participantId, int x, int y);

    /// <summary>
    /// 마우스 클릭 입력 처리
    /// </summary>
    /// <param name="participantId">참가자 ID</param>
    /// <param name="button">마우스 버튼</param>
    /// <param name="isPressed">눌림 여부</param>
    /// <returns>입력 처리 여부 (true=처리됨, false=차단됨)</returns>
    bool ProcessMouseClick(string participantId, MouseButton button, bool isPressed);

    /// <summary>
    /// 마우스 휠 입력 처리
    /// </summary>
    /// <param name="participantId">참가자 ID</param>
    /// <param name="delta">휠 델타</param>
    /// <returns>입력 처리 여부 (true=처리됨, false=차단됨)</returns>
    bool ProcessMouseWheel(string participantId, int delta);

    /// <summary>
    /// 키보드 입력 처리
    /// </summary>
    /// <param name="participantId">참가자 ID</param>
    /// <param name="keyCode">키 코드</param>
    /// <param name="isPressed">눌림 여부</param>
    /// <returns>입력 처리 여부 (true=처리됨, false=차단됨)</returns>
    bool ProcessKeyboardInput(string participantId, int keyCode, bool isPressed);
}

/// <summary>
/// WDS 제어 레벨
/// </summary>
public enum ControlLevel
{
    /// <summary>
    /// 보기 전용 (입력 불가)
    /// </summary>
    ViewOnly = 0,

    /// <summary>
    /// 키보드 입력만 허용
    /// </summary>
    KeyboardOnly = 1,

    /// <summary>
    /// 키보드 + 마우스 입력 허용 (일반 제어)
    /// </summary>
    KeyboardAndMouse = 2,

    /// <summary>
    /// 전체 제어 (키보드 + 마우스 + 시스템 명령)
    /// </summary>
    FullControl = 3
}

/// <summary>
/// 제어 권한 상태
/// </summary>
public sealed class ControlPermission
{
    public string ParticipantId { get; init; } = string.Empty;
    public ControlLevel Level { get; set; }
    public bool HasControl { get; set; }
    public DateTimeOffset GrantedAt { get; init; }
}

/// <summary>
/// 마우스 버튼
/// </summary>
public enum MouseButton
{
    /// <summary>
    /// 왼쪽 버튼
    /// </summary>
    Left,

    /// <summary>
    /// 오른쪽 버튼
    /// </summary>
    Right,

    /// <summary>
    /// 가운데 버튼
    /// </summary>
    Middle,

    /// <summary>
    /// X1 버튼
    /// </summary>
    XButton1,

    /// <summary>
    /// X2 버튼
    /// </summary>
    XButton2
}
