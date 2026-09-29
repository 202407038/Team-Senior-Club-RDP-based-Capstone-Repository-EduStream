using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// Windows OS 실제 네이티브 입력 주입 파이프라인 (Win32 user32.dll API 기반)
/// 대상 검증, 눌림 상태 추적, Win32 API 호출 및 단절/차단 릴리즈를 단일 락 경계로 원자화하여 Stuck 방지
/// </summary>
public sealed class WindowsNativeInputPipeline : INativeInputPipeline
{
    private readonly object _lock = new();
    private bool _isConnected;
    private string? _activeTargetId;
    private readonly HashSet<MouseButton> _heldMouseButtons = new();
    private readonly HashSet<int> _heldKeys = new();

    public bool IsConnected
    {
        get { lock (_lock) return _isConnected; }
    }

    public string? ActiveTargetId
    {
        get { lock (_lock) return _activeTargetId; }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _isConnected = true;
        }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _isConnected = false;
            _activeTargetId = null;
            ReleaseAllHeldInputsLocked();
        }
        return Task.CompletedTask;
    }

    public Task InjectInputAsync(string targetId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        lock (_lock)
        {
            if (!_isConnected)
            {
                throw new InputPipelineException("Windows 네이티브 입력 엔진이 연결되어 있지 않습니다.");
            }

            if (_activeTargetId != null && _activeTargetId != targetId)
            {
                ReleaseAllHeldInputsLocked();
            }

            _activeTargetId = targetId;
        }
        return Task.CompletedTask;
    }

    public Task BlockInputAsync(string targetId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        lock (_lock)
        {
            if (_activeTargetId == targetId)
            {
                _activeTargetId = null;
                ReleaseAllHeldInputsLocked();
            }
        }
        return Task.CompletedTask;
    }

    public Task InjectMouseMoveAsync(string targetId, int x, int y, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            ValidateTargetAndConnectionLocked(targetId);

            if (OperatingSystem.IsWindows())
            {
                if (!SetCursorPos(x, y))
                {
                    throw new InputPipelineException($"마우스 좌표 이동 실패 (X: {x}, Y: {y})");
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task InjectMouseClickAsync(string targetId, MouseButton button, bool isPressed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        uint flags = button switch
        {
            MouseButton.Left => isPressed ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            MouseButton.Right => isPressed ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            MouseButton.Middle => isPressed ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, "지원되지 않는 마우스 버튼입니다.")
        };

        lock (_lock)
        {
            ValidateTargetAndConnectionLocked(targetId);

            if (OperatingSystem.IsWindows())
            {
                if (isPressed)
                {
                    _heldMouseButtons.Add(button);
                }
                else
                {
                    _heldMouseButtons.Remove(button);
                }

                mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
            }
        }
        return Task.CompletedTask;
    }

    public Task InjectMouseWheelAsync(string targetId, int delta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            ValidateTargetAndConnectionLocked(targetId);

            if (OperatingSystem.IsWindows())
            {
                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
            }
        }
        return Task.CompletedTask;
    }

    public Task InjectKeyboardInputAsync(string targetId, int keyCode, bool isPressed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        uint flags = isPressed ? 0u : KEYEVENTF_KEYUP;

        lock (_lock)
        {
            ValidateTargetAndConnectionLocked(targetId);

            if (OperatingSystem.IsWindows())
            {
                if (isPressed)
                {
                    _heldKeys.Add(keyCode);
                }
                else
                {
                    _heldKeys.Remove(keyCode);
                }

                keybd_event((byte)keyCode, 0, flags, UIntPtr.Zero);
            }
        }
        return Task.CompletedTask;
    }

    private void ValidateTargetAndConnectionLocked(string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        if (!_isConnected)
        {
            throw new InputPipelineException("Windows 네이티브 입력 엔진이 연결되어 있지 않습니다.");
        }

        if (_activeTargetId != targetId)
        {
            throw new InputPipelineException($"선택되지 않은 대상 ({targetId})의 입력 주입은 차단되었습니다. 활성 대상: {_activeTargetId ?? "없음"}");
        }
    }

    private void ReleaseAllHeldInputsLocked()
    {
        if (!OperatingSystem.IsWindows()) return;

        foreach (var button in _heldMouseButtons)
        {
            uint flags = button switch
            {
                MouseButton.Left => MOUSEEVENTF_LEFTUP,
                MouseButton.Right => MOUSEEVENTF_RIGHTUP,
                MouseButton.Middle => MOUSEEVENTF_MIDDLEUP,
                _ => 0
            };
            if (flags != 0)
            {
                mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
            }
        }
        _heldMouseButtons.Clear();

        foreach (var keyCode in _heldKeys)
        {
            keybd_event((byte)keyCode, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        _heldKeys.Clear();
    }

    #region Win32 Native Interop
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    #endregion
}
