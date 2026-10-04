using EduStream.Core.Logging;
using EduStream.Core.Network;

namespace EduStream.FileTransfer.Tests;

public sealed class SecureFrameCancellationTests
{
    [Fact]
    public async Task CancelDuringWrite_FinishesFrame_AndNextFrameRemainsReadable()
    {
        using var cancel = new CancellationTokenSource();
        using var stream = new CancelAfterPrefixStream(cancel);
        await using var connection = new SecureCollaborationConnection(stream, null, new InMemoryLogSink());
        var first = new byte[] { 1, 2, 3, 4 };
        await connection.SendAsync(first, cancel.Token);
        Assert.True(cancel.IsCancellationRequested);
        await connection.SendAsync(new byte[] { 9, 8 });
        stream.Position = 0;
        Assert.Equal(first, await CollaborationFraming.ReadAsync(stream));
        Assert.Equal(new byte[] { 9, 8 }, await CollaborationFraming.ReadAsync(stream));
        Assert.Null(await CollaborationFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task CancelBeforeWrite_SendsNothing_AndConnectionRemainsUsable()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var stream = new MemoryStream();
        await using var connection = new SecureCollaborationConnection(stream, null, new InMemoryLogSink());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connection.SendAsync(new byte[] { 1 }, cancel.Token));
        Assert.Equal(0, stream.Length);
        await connection.SendAsync(new byte[] { 2 });
        stream.Position = 0;
        Assert.Equal(new byte[] { 2 }, await CollaborationFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task TransportFailureAfterPrefix_ClosesConnection_RejectsFollowingSend()
    {
        using var stream = new FailingStream();
        await using var connection = new SecureCollaborationConnection(stream, null, new InMemoryLogSink());
        await Assert.ThrowsAsync<IOException>(() => connection.SendAsync(new byte[] { 1, 2 }));
        Assert.True(connection.IsClosed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.SendAsync(new byte[] { 3 }));
    }

    // 실제 스트림 WriteAsync는 취소 전에 일부 바이트를 기록했을 수 있다.
    // 타이밍/부하에 의존하지 않고 이 경계를 재현한다.
    private sealed class CancelAfterPrefixStream(CancellationTokenSource caller) : MemoryStream
    {
        private bool _first = true;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (!_first) { await base.WriteAsync(buffer, token); return; }
            _first = false;
            await base.WriteAsync(buffer[..4], token);
            caller.Cancel();
            token.ThrowIfCancellationRequested();
            await base.WriteAsync(buffer[4..], token);
        }
    }

    private sealed class FailingStream : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await base.WriteAsync(buffer[..4], token);
            throw new IOException("simulated partial transport write");
        }
    }
}
