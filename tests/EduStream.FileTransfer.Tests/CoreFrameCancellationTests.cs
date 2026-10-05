using EduStream.Core.Logging;
using EduStream.Core.Network;

namespace EduStream.FileTransfer.Tests;

public sealed class CoreFrameCancellationTests
{
    [Fact]
    public async Task RequestCancellationDuringWrite_PreservesWholeFrameAndNextRequest()
    {
        await using var stream = new InterruptedWriteStream();
        await using var connection = new SecureCollaborationConnection(stream, null, new InMemoryLogSink());
        using var cancel = new CancellationTokenSource();
        var first = connection.SendAsync(new byte[] { 10, 20, 30 }, cancel.Token);
        await stream.HeaderWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        stream.Resume.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.SendAsync(new byte[] { 40, 50 });
        using var received = new MemoryStream(stream.Bytes.ToArray());
        Assert.Equal(new byte[] { 10, 20, 30 }, await CollaborationFraming.ReadAsync(received));
        Assert.Equal(new byte[] { 40, 50 }, await CollaborationFraming.ReadAsync(received));
        Assert.Null(await CollaborationFraming.ReadAsync(received));
        Assert.False(connection.IsClosed);
    }

    [Fact]
    public async Task WaitingRequestCancellation_DoesNotWriteOrCloseConnection()
    {
        await using var stream = new InterruptedWriteStream();
        await using var connection = new SecureCollaborationConnection(stream, null, new InMemoryLogSink());
        var first = connection.SendAsync(new byte[] { 10 });
        await stream.HeaderWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var waiting = connection.SendAsync(new byte[] { 20 }, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        stream.Resume.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        using var received = new MemoryStream(stream.Bytes.ToArray());
        Assert.Equal(new byte[] { 10 }, await CollaborationFraming.ReadAsync(received));
        Assert.Null(await CollaborationFraming.ReadAsync(received));
        Assert.False(connection.IsClosed);
    }

    [Fact]
    public async Task PartialWriteFailure_ClosesConnectionInsteadOfAppendingAnotherFrame()
    {
        await using var stream = new InterruptedWriteStream { Fail = true };
        await using var connection = new SecureCollaborationConnection(stream, null, new InMemoryLogSink());
        var sending = connection.SendAsync(new byte[] { 10 });
        await stream.HeaderWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stream.Resume.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => sending);
        Assert.True(connection.IsClosed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.SendAsync(new byte[] { 20 }));
        Assert.Equal(CollaborationFraming.HeaderBytes, stream.Bytes.Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BlockedWrite_ExpiresAndClosesConnection_WithoutHoldingNextSender(bool cancelRequest, bool ignoreToken)
    {
        await using var stream = new InterruptedWriteStream { IgnoreToken = ignoreToken };
        await using var connection = new SecureCollaborationConnection(stream, null, new InMemoryLogSink(),
            frameWriteTimeout: TimeSpan.FromMilliseconds(300));
        using var cancel = new CancellationTokenSource();
        var first = connection.SendAsync(new byte[] { 10, 20 }, cancel.Token);
        await stream.HeaderWritten.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var next = connection.SendAsync(new byte[] { 30 });
        if (cancelRequest) cancel.Cancel();
        try
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("프레임 송신", error.Message); // 바깥 테스트 제한 시간에 걸린 것을 성공으로 오인하지 않는다.
            await AssertClosedSenderAsync(next);
            Assert.True(connection.IsClosed);
            await connection.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.SendAsync(new byte[] { 40 }));
            Assert.Equal(CollaborationFraming.HeaderBytes, stream.Bytes.Length); // 다음 프레임은 쓰이지 않았다.
        }
        finally
        {
            stream.Resume.TrySetResult();
            await stream.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task DisposeDuringBlockedWrite_ReleasesActiveAndWaitingSender()
    {
        await using var stream = new InterruptedWriteStream();
        await using var connection = new SecureCollaborationConnection(stream, null, new InMemoryLogSink());
        var first = connection.SendAsync(new byte[] { 10 });
        await stream.HeaderWritten.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var next = connection.SendAsync(new byte[] { 20 });
        await connection.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(3)));
        await AssertClosedSenderAsync(next);
        Assert.True(connection.IsClosed);
    }

    private static async Task AssertClosedSenderAsync(Task sender)
    {
        var error = await Record.ExceptionAsync(() => sender.WaitAsync(TimeSpan.FromSeconds(3)));
        // 연결 종료가 잠금 대기를 취소하거나, 잠금을 얻은 직후 disposed 검사에 걸릴 수 있다.
        // 두 종료 경로만 허용하며 성공·시간 초과·다른 예외는 실패로 처리한다.
        Assert.True(error is OperationCanceledException or ObjectDisposedException,
            $"Expected connection closure, got {error?.GetType().Name ?? "success"}.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483648)]
    public void InvalidFrameDeadline_IsRejected(double milliseconds)
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SecureCollaborationConnection(stream, null,
            new InMemoryLogSink(), frameWriteTimeout: TimeSpan.FromMilliseconds(milliseconds)));
    }

    private sealed class InterruptedWriteStream : MemoryStream
    {
        public MemoryStream Bytes { get; } = new();
        public TaskCompletionSource HeaderWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }
        public bool IgnoreToken { get; init; }
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await Bytes.WriteAsync(buffer[..CollaborationFraming.HeaderBytes], cancellationToken);
                HeaderWritten.TrySetResult();
                await Resume.Task.WaitAsync(IgnoreToken ? CancellationToken.None : cancellationToken);
                if (Fail) throw new IOException("simulated partial write");
                await Bytes.WriteAsync(buffer[CollaborationFraming.HeaderBytes..], cancellationToken);
            }
            finally { Finished.TrySetResult(); }
        }
    }
}
