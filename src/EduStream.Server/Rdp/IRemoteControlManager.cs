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
