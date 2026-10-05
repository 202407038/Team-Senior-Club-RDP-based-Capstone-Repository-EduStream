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

    private sealed class InterruptedWriteStream : MemoryStream
    {
        public MemoryStream Bytes { get; } = new();
        public TaskCompletionSource HeaderWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Bytes.WriteAsync(buffer[..CollaborationFraming.HeaderBytes], cancellationToken);
            HeaderWritten.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            if (Fail) throw new IOException("simulated partial write");
            await Bytes.WriteAsync(buffer[CollaborationFraming.HeaderBytes..], cancellationToken);
        }
    }
}
