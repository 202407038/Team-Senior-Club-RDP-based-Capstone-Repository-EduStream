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
/// 네이티브 입력 엔진 연동 및 실제 PC 입력 적용/차단 파이프라인 지원
/// </summary>
public sealed class RemoteControlManager : IRemoteControlManager, IRemoteInputGate
{
    private readonly ConcurrentDictionary<string, ControlPermission> _permissions = new();
    private ControlLevel _currentLevel = ControlLevel.ViewOnly;
    private readonly object _gateLock = new();
    private RemoteControlState? _activeControlState;
    private INativeInputPipeline _nativeInputPipeline = UnavailableNativeInputPipeline.Instance;
    private bool _isNativeEngineConnected = false;

    public ControlLevel CurrentControlLevel => _currentLevel;
    public bool IsNativeEngineConnected => _isNativeEngineConnected;

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
        if (!CanProcessInput(participantId, allowMouse: true))
            return false;

        if (!_isNativeEngineConnected || !_nativeInputPipeline.IsConnected)
            return true;

        try
        {
            _nativeInputPipeline.InjectMouseMoveAsync(participantId, x, y).GetAwaiter().GetResult();
            return true;
        }
         catch
        {
            return false;
        }
    }

    public bool ProcessMouseClick(string participantId, MouseButton button, bool isPressed)
    {
        if (!CanProcessInput(participantId, allowMouse: true))
            return false;

        if (!_isNativeEngineConnected || !_nativeInputPipeline.IsConnected)
            return true;

        try
        {
            _nativeInputPipeline.InjectMouseClickAsync(participantId, button, isPressed).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool ProcessMouseWheel(string participantId, int delta)
    {
        if (!CanProcessInput(participantId, allowMouse: true))
            return false;

        if (!_isNativeEngineConnected || !_nativeInputPipeline.IsConnected)
            return true;

        try
        {
            _nativeInputPipeline.InjectMouseWheelAsync(participantId, delta).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool ProcessKeyboardInput(string participantId, int keyCode, bool isPressed)
    {
        if (!CanProcessInput(participantId, allowKeyboard: true))
            return false;

        if (!_isNativeEngineConnected || !_nativeInputPipeline.IsConnected)
            return true;

        try
        {
            _nativeInputPipeline.InjectKeyboardInputAsync(participantId, keyCode, isPressed).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
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
        // 네이티브 입력 엔진 연결 상태 확인
        if (!_isNativeEngineConnected || !_nativeInputPipeline.IsConnected)
        {
            throw new InputPipelineException("네이티브 입력 엔진이 연결되지 않아 입력 허용을 진행할 수 없습니다.");
        }

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

        // 실제 네이티브 입력 허용(Inject) 확인
        if (requested.Student != null)
        {
            var targetId = requested.Student.ConnectionId.ToString();
            try
            {
                await _nativeInputPipeline.InjectInputAsync(targetId, cancellationToken);
            }
            catch (InputPipelineException ex)
            {
                // 네이티브 허용 실패 시 권한 철회
                lock (_gateLock)
                {
                    if (requested.Student != null)
                    {
                        var connectionIdStr = requested.Student.ConnectionId.ToString();
                        if (_permissions.ContainsKey(connectionIdStr))
                        {
                            _permissions[connectionIdStr].HasControl = false;
                        }
                    }
                    _activeControlState = null;
                }
                throw new InputPipelineException($"네이티브 입력 허용 실패: {ex.Message}", ex);
            }
        }
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

        // 실제 네이티브 입력 차단(Block) 확인
        if (revoked.Student != null && _isNativeEngineConnected)
        {
            var targetId = revoked.Student.ConnectionId.ToString();
            try
            {
                await _nativeInputPipeline.BlockInputAsync(targetId, cancellationToken);
            }
            catch (InputPipelineException ex)
            {
                // 차단 실패 시 예외를 상위로 전파하여 실패 상태를 알림
                throw new InputPipelineException($"네이티브 입력 차단 실패: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// 네이티브 입력 엔진 연결
    /// </summary>
    public async Task ConnectNativeEngineAsync(INativeInputPipeline pipeline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        lock (_gateLock)
        {
            if (_isNativeEngineConnected)
                throw new InvalidOperationException("네이티브 입력 엔진이 이미 연결되어 있습니다.");
        }

        try
        {
            await pipeline.ConnectAsync(cancellationToken);
            lock (_gateLock)
            {
                _nativeInputPipeline = pipeline;
                _isNativeEngineConnected = true;
            }
        }
        catch (InputPipelineException ex)
        {
            throw new InputPipelineException($"네이티브 입력 엔진 연결 실패: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 네이티브 입력 엔진 연결 해제
    /// </summary>
    public async Task DisconnectNativeEngineAsync(CancellationToken cancellationToken = default)
    {
        lock (_gateLock)
        {
            if (!_isNativeEngineConnected)
                return;
        }

        try
        {
            await _nativeInputPipeline.DisconnectAsync(cancellationToken);
            lock (_gateLock)
            {
                _nativeInputPipeline = UnavailableNativeInputPipeline.Instance;
                _isNativeEngineConnected = false;
                _activeControlState = null;
            }
        }
        catch (InputPipelineException ex)
        {
            throw new InputPipelineException($"네이티브 입력 엔진 연결 해제 실패: {ex.Message}", ex);
        }
    }
}
