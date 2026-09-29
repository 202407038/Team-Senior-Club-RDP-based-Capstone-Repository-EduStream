using System;
using System.Threading;
using System.Threading.Tasks;

namespace EduStream.Server.Rdp;

/// <summary>
/// 네이티브 입력 엔진 파이프라인 인터페이스
/// 실제 PC 입력 적용 (Inject) 및 차단 (Block)을 담당
/// </summary>
public interface INativeInputPipeline
{
    /// <summary>
    /// 입력 엔진이 연결되어 있는지 확인
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 대상 PC에 입력 허용 (Inject) 적용
    /// </summary>
    Task InjectInputAsync(string targetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 마우스 이동 입력 주입
    /// </summary>
    Task InjectMouseMoveAsync(string targetId, int x, int y, CancellationToken cancellationToken = default);

    /// <summary>
    /// 마우스 클릭 입력 주입
    /// </summary>
    Task InjectMouseClickAsync(string targetId, MouseButton button, bool isPressed, CancellationToken cancellationToken = default);

    /// <summary>
    /// 마우스 휠 입력 주입
    /// </summary>
    Task InjectMouseWheelAsync(string targetId, int delta, CancellationToken cancellationToken = default);

    /// <summary>
    /// 키보드 입력 주입
    /// </summary>
    Task InjectKeyboardInputAsync(string targetId, int keyCode, bool isPressed, CancellationToken cancellationToken = default);

    /// <summary>
    /// 대상 PC 입력 차단 (Block) 적용
    /// </summary>
    Task BlockInputAsync(string targetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 입력 엔진 연결
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 입력 엔진 연결 해제
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 네이티브 입력 엔진 미연결 상태 기본 구현
/// </summary>
public sealed class UnavailableNativeInputPipeline : INativeInputPipeline
{
    public static UnavailableNativeInputPipeline Instance { get; } = new();

    private UnavailableNativeInputPipeline() { }

    public bool IsConnected => false;

    public Task InjectInputAsync(string targetId, CancellationToken cancellationToken = default)
        => Task.FromException(new InputPipelineException("네이티브 입력 엔진이 연결되지 않았습니다."));

    public Task InjectMouseMoveAsync(string targetId, int x, int y, CancellationToken cancellationToken = default)
        => Task.FromException(new InputPipelineException("네이티브 입력 엔진이 연결되지 않았습니다."));

    public Task InjectMouseClickAsync(string targetId, MouseButton button, bool isPressed, CancellationToken cancellationToken = default)
        => Task.FromException(new InputPipelineException("네이티브 입력 엔진이 연결되지 않았습니다."));

    public Task InjectMouseWheelAsync(string targetId, int delta, CancellationToken cancellationToken = default)
        => Task.FromException(new InputPipelineException("네이티브 입력 엔진이 연결되지 않았습니다."));

    public Task InjectKeyboardInputAsync(string targetId, int keyCode, bool isPressed, CancellationToken cancellationToken = default)
        => Task.FromException(new InputPipelineException("네이티브 입력 엔진이 연결되지 않았습니다."));

    public Task BlockInputAsync(string targetId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => Task.FromException(new InputPipelineException("네이티브 입력 엔진을 연결할 수 없습니다."));

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// 입력 파이프라인 예외
/// </summary>
public sealed class InputPipelineException : System.Exception
{
    public InputPipelineException(string message) : base(message) { }
    public InputPipelineException(string message, System.Exception innerException) : base(message, innerException) { }
}
