using System;
using System.Threading;
using System.Threading.Tasks;
using EduStream.Core.Collaboration;
using EduStream.Server.Services;

namespace EduStream.Server.Rdp;

/// <summary>
/// 2번 승인/회수 흐름(<see cref="IRemoteInputGate"/>)과 3번 역방향 WDS 세션 제어를 잇는 어댑터.
/// 2번 서비스·UI는 수정하지 않습니다. 2번은 아래 호출 예처럼 이 인스턴스만 붙이면 됩니다.
/// </summary>
/// <remarks>
/// <para>
/// 호출 예 (2번 쪽 코드 — 이 PR에서는 작성하지 않음):
/// <code>
/// var gate = new ReverseWdsRemoteInputGate(
///     reverseSession.GrantControlAsync,
///     reverseSession.RevokeControlAsync,
///     state => professorIdByConnection[state.Professor.ParticipantId],
///     osInputPipeline); // 선택. OS 수준 입력 게이트. WDS 뷰어발 입력과는 별개.
/// sessionManager.AttachRemoteInputGate(gate);
/// coordinator.SetInputGate(gate);
/// </code>
/// </para>
/// <para>
/// 이 게이트가 실제로 수행하는 것:
/// 1) WDS 원격 제어 — <see cref="ReverseSessionManager.GrantControlAsync"/> / <see cref="ReverseSessionManager.RevokeControlAsync"/>
///    로 호스트 COM 의 ControlLevel 을 3↔2 로 바꿉니다. 이게 역방향 세션의 제어 허용/회수입니다.
/// 2) (선택) OS 입력 파이프라인 — <see cref="INativeInputPipeline"/> 으로 같은 PC 의 커서를 움직이거나 막습니다.
///    이 경로는 교수자 WDS 뷰어가 보낸 마우스/키보드가 학생 화면에 도달하는 경로가 아닙니다.
/// </para>
/// <para>
/// 수행하지 않는 것: WDS 뷰어 원본 입력의 학생 PC 도달 검증. 그 경로는 이 클래스 밖에 있으며 미검증으로 남깁니다.
/// </para>
/// </remarks>
public sealed class ReverseWdsRemoteInputGate : IRemoteInputGate
{
    private readonly Func<string, CancellationToken, Task> _grantWdsControl;
    private readonly Func<string, CancellationToken, Task> _revokeWdsControl;
    private readonly Func<RemoteControlState, string> _resolveProfessorId;
    private readonly INativeInputPipeline? _osInput;

    /// <param name="grantWdsControl">역방향 세션의 실제 제어 허용. 보통 <see cref="ReverseSessionManager.GrantControlAsync"/>.</param>
    /// <param name="revokeWdsControl">역방향 세션의 실제 제어 회수. 보통 <see cref="ReverseSessionManager.RevokeControlAsync"/>.</param>
    /// <param name="resolveProfessorId">
    /// 2번 <see cref="RemoteControlState"/> 의 교수자 연결을 역방향 세션이 쓰는 교수자 ID 문자열로 바꿉니다.
    /// (2번 연결은 Guid, 역방향 초대는 문자열 ID 를 쓰므로 매핑은 호출자가 정합니다.)
    /// </param>
    /// <param name="osInput">
    /// 선택. 같은 PC 의 OS 입력 허용/차단. 연결되지 않았거나 null 이면 WDS ControlLevel 만 바꿉니다.
    /// </param>
    public ReverseWdsRemoteInputGate(
        Func<string, CancellationToken, Task> grantWdsControl,
        Func<string, CancellationToken, Task> revokeWdsControl,
        Func<RemoteControlState, string> resolveProfessorId,
        INativeInputPipeline? osInput = null)
    {
        _grantWdsControl = grantWdsControl ?? throw new ArgumentNullException(nameof(grantWdsControl));
        _revokeWdsControl = revokeWdsControl ?? throw new ArgumentNullException(nameof(revokeWdsControl));
        _resolveProfessorId = resolveProfessorId ?? throw new ArgumentNullException(nameof(resolveProfessorId));
        _osInput = osInput;
    }

    /// <summary>역방향 세션 인스턴스를 바로 붙이는 편의 생성자.</summary>
    public ReverseWdsRemoteInputGate(
        ReverseSessionManager session,
        Func<RemoteControlState, string> resolveProfessorId,
        INativeInputPipeline? osInput = null)
        : this(
            session.GrantControlAsync,
            session.RevokeControlAsync,
            resolveProfessorId,
            osInput)
    {
        ArgumentNullException.ThrowIfNull(session);
    }

    public async Task GrantAsync(RemoteControlState requested, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);
        cancellationToken.ThrowIfCancellationRequested();

        var professorId = RequireProfessorId(requested);

        // 1) WDS 원격 제어 허용 (호스트 COM ControlLevel=3). 실패하면 OS 입력도 열지 않는다.
        await _grantWdsControl(professorId, cancellationToken).ConfigureAwait(false);

        // 2) (선택) 같은 PC OS 입력 게이트. 뷰어발 WDS 입력 경로가 아님.
        if (_osInput is { IsConnected: true })
            await _osInput.InjectInputAsync(professorId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAsync(RemoteControlState revoked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revoked);
        var professorId = RequireProfessorId(revoked);

        // 회수는 멱등: WDS 쪽이 이미 끊겨 있어도 예외를 삼키지 않고 세션 구현의 멱등 계약을 따른다.
        await _revokeWdsControl(professorId, cancellationToken).ConfigureAwait(false);

        if (_osInput is { IsConnected: true })
            await _osInput.BlockInputAsync(professorId, cancellationToken).ConfigureAwait(false);
    }

    private string RequireProfessorId(RemoteControlState state)
    {
        var professorId = _resolveProfessorId(state);
        if (string.IsNullOrWhiteSpace(professorId))
            throw new ArgumentException("RemoteControlState 에서 역방향 세션용 교수자 ID 를 해석하지 못했습니다.");
        return professorId;
    }
}
