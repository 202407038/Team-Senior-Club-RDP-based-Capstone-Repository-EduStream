using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 원격 제어 입력 레벨 관리자 프로토타입 구현
/// EduStream.Core에 의존하지 않고 Server 내부에서 독립 동작
/// </summary>
public sealed class RemoteControlManager : IRemoteControlManager
{
    private readonly ConcurrentDictionary<string, ControlPermission> _permissions = new();
    private ControlLevel _currentLevel = ControlLevel.ViewOnly;

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
}
