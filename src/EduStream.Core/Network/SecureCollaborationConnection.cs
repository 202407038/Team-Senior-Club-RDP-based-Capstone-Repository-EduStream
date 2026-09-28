using EduStream.Core.Collaboration;
using EduStream.Core.Logging;

namespace EduStream.Core.Network;

/// <summary>
/// TLS 인증·capability 협상을 마친 보호 채널 연결 하나입니다. 교수자·학생 양쪽이 같은 구현을 씁니다.
/// 송신은 프레임 단위로 직렬화하고, 수신은 <see cref="Start"/>에 넘긴 처리기를 프레임 순서대로 await합니다.
/// </summary>
public sealed class SecureCollaborationConnection : ICollaborationChannel, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IDisposable? _transport;
    private readonly ILogSink _logSink;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    private int _disposed;

    /// <param name="stream">인증이 끝난 SslStream.</param>
    /// <param name="transport">스트림과 함께 닫을 하위 소켓(TcpClient 등).</param>
    /// <param name="remoteAddress">상대 IP. 비밀번호 시도 제한 키로 씁니다.</param>
    public SecureCollaborationConnection(Stream stream, IDisposable? transport, ILogSink logSink, string? remoteAddress = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _transport = transport;
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        RemoteAddress = remoteAddress ?? "unknown";
    }

    public string RemoteAddress { get; }

    /// <summary>이 프로세스 안에서만 쓰는 연결 식별자입니다. 참가자 ConnectionId와는 별개입니다.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>수신 루프가 끝나면(상대 종료·오류·Dispose) 완료됩니다.</summary>
    public Task Completion => _completion.Task;

    public bool IsClosed => Volatile.Read(ref _disposed) != 0 || _completion.Task.IsCompleted;

    /// <summary>수신 루프를 시작합니다. 한 번만 호출할 수 있습니다.</summary>
    public void Start(Func<byte[], Task> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("수신 루프가 이미 시작되었습니다.");
        _ = ReceiveLoopAsync(onFrame);
    }

    public async Task SendAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _sendLock.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await CollaborationFraming.WriteAsync(_stream, frame, linked.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(Func<byte[], Task> onFrame)
    {
        var reason = "상대 종료";
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var frame = await CollaborationFraming.ReadAsync(_stream, _lifetime.Token);
                if (frame is null) break;
                try
                {
                    await onFrame(frame);
                }
                catch (Exception ex)
                {
                    // 처리기 오류 하나로 연결 전체를 끊지 않는다. 경로·비밀값이 담길 수 있어 메시지는 남기지 않는다.
                    _logSink.Write($"[Secure] 프레임 처리 오류: connection={Id}, {ex.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            reason = "로컬 종료";
        }
        catch (CollaborationException ex)
        {
            reason = $"프레임 거부({ex.Code})";
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
        }
        finally
        {
            _logSink.Write($"[Secure] 수신 종료: connection={Id}, 사유={reason}");
            await DisposeAsync();
            _completion.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try { await _stream.DisposeAsync(); } catch { }
        try { _transport?.Dispose(); } catch { }
        // 수신 루프를 시작하지 않은 연결도 Completion을 기다리는 쪽이 멈추지 않게 한다.
        if (Volatile.Read(ref _started) == 0) _completion.TrySetResult();
        // 대기 중인 송신자의 finally/Release가 남을 수 있어 SemaphoreSlim과 CTS는 Dispose하지 않는다.
    }
}
