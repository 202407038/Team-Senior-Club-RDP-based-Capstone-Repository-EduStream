using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using EduStream.Core.Collaboration;
using EduStream.Server.Services;

namespace EduStream.Server.Rdp;

/// <summary>
/// 원격 제어 입력 레벨 관리자 프로토타입 구현
/// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
/// IRemoteInputGate/ServerRemoteControlCoordinator와 연동하여 실제 입력 허용/차단을 처리
/// </summary>
public sealed class RemoteControlManager : IRemoteControlManager, IRemoteInputGate
{
    private readonly ConcurrentDictionary<string, ControlPermission> _permissions = new();
    private ControlLevel _currentLevel = ControlLevel.ViewOnly;
    private readonly object _gateLock = new();
    private RemoteControlState? _activeControlState;

    public ControlLevel CurrentControlLevel => _currentLevel;

    public Task SetControlLevelAsync(ControlLevel level, CancellationToken cancellationToken = default)
    {
        _currentLevel = level;

        // 제어 레벨이 ViewOnly로 변경되면 모든 권한 철회
        if (level == ControlLevel.ViewOnly)
        {
            foreach (var permission in _permissions.Values)
            {
                permission.HasControl = false;
            }
        }

        return Task.CompletedTask;
    }

    public Task GrantControlAsync(string participantId, CancellationToken cancellationToken = default)
    {
        if (_currentLevel == ControlLevel.ViewOnly)
            throw new InvalidOperationException("현재 제어 레벨이 보기 전용입니다.");

        _permissions[participantId] = new ControlPermission
        {
            ParticipantId = participantId,
            Level = _currentLevel,
            HasControl = true,
            GrantedAt = DateTimeOffset.UtcNow
        };

        return Task.CompletedTask;
    }

    public Task RevokeControlAsync(string participantId, CancellationToken cancellationToken = default)
    {
        if (_permissions.TryGetValue(participantId, out var permission))
        {
            permission.HasControl = false;
        }

        return Task.CompletedTask;
    }

    public Task RevokeAllControlAsync(CancellationToken cancellationToken = default)
    {
        foreach (var permission in _permissions.Values)
        {
            permission.HasControl = false;
        }

        return Task.CompletedTask;
    }

    public Task<ControlPermission> GetParticipantPermissionAsync(string participantId, CancellationToken cancellationToken = default)
    {
        if (_permissions.TryGetValue(participantId, out var permission))
        {
            return Task.FromResult(permission);
        }

        return Task.FromResult(new ControlPermission
        {
            ParticipantId = participantId,
            Level = ControlLevel.ViewOnly,
            HasControl = false,
            GrantedAt = DateTimeOffset.MinValue
        });
    }

    public bool ProcessMouseMove(string participantId, int x, int y)
    {
        return CanProcessInput(participantId, allowMouse: true);
    }

    public bool ProcessMouseClick(string participantId, MouseButton button, bool isPressed)
    {
        return CanProcessInput(participantId, allowMouse: true);
    }

    public bool ProcessMouseWheel(string participantId, int delta)
    {
        return CanProcessInput(participantId, allowMouse: true);
    }

    public bool ProcessKeyboardInput(string participantId, int keyCode, bool isPressed)
    {
        return CanProcessInput(participantId, allowKeyboard: true);
    }

    private bool CanProcessInput(string participantId, bool allowMouse = false, bool allowKeyboard = false)
    {
        // 제어 레벨이 ViewOnly이면 모든 입력 차단
        if (_currentLevel == ControlLevel.ViewOnly)
            return false;

        // 참가자 권한 확인
        if (!_permissions.TryGetValue(participantId, out var permission))
            return false;

        if (!permission.HasControl)
            return false;

        // 제어 레벨에 따른 입력 필터링
        if (allowMouse && _currentLevel == ControlLevel.KeyboardOnly)
            return false;

        if (allowKeyboard && _currentLevel == ControlLevel.ViewOnly)
            return false;

        return true;
    }

    // IRemoteInputGate 구현 - ServerRemoteControlCoordinator와 연동
    public async Task GrantAsync(RemoteControlState requested, CancellationToken cancellationToken)
    {
        lock (_gateLock)
        {
            _activeControlState = requested;
            // 요청된 참가자에게 제어 권한 부여
            if (requested.Student != null)
            {
                var connectionIdStr = requested.Student.ConnectionId.ToString();
                _permissions[connectionIdStr] = new ControlPermission
                {
                    ParticipantId = connectionIdStr,
                    Level = _currentLevel,
                    HasControl = true,
                    GrantedAt = DateTimeOffset.UtcNow
                };
            }
        }
        await Task.CompletedTask;
    }

    public async Task RevokeAsync(RemoteControlState revoked, CancellationToken cancellationToken)
    {
        lock (_gateLock)
        {
            if (revoked.Student != null)
            {
                var connectionIdStr = revoked.Student.ConnectionId.ToString();
                if (_permissions.ContainsKey(connectionIdStr))
                {
                    _permissions[connectionIdStr].HasControl = false;
                }
            }
            _activeControlState = null;
        }
        await Task.CompletedTask;
    }
}
