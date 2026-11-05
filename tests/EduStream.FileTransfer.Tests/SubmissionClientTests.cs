using EduStream.Client.Services;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class SubmissionClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EduStream-SubmissionClient-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingStoredAcknowledgementIsNotReportedAsSuccess(bool disconnect)
    {
        Directory.CreateDirectory(_root); var source = Path.Combine(_root, "source.txt"); await File.WriteAllTextAsync(source, "synthetic");
        var session = Guid.NewGuid(); var student = Guid.NewGuid();
        using var store = new SubmissionStore(session, _root); store.SetOpen(true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SubmissionClient client = null!;
        client = new(session, new CallbackChannel(frame =>
        {
            var message = CollaborationMessageCodec.Decode<SubmissionMessage>(frame, out _);
            var response = store.Handle(student, "학생", message);
            if (message.Action == SubmissionAction.Complete)
            {
                if (disconnect) client.ConnectionClosed(); else cancellation.Cancel();
            }
            else client.Receive(response);
        }));
        client.Receive(new(session, Guid.Empty, SubmissionAction.State, IsOpen: true));
        var failure = await Record.ExceptionAsync(() => client.SubmitAsync(source, token: cancellation.Token));
        Assert.NotNull(failure);
        Assert.True(failure is IOException or OperationCanceledException);
        // 서버 파일이 있어도 수신 확인을 잃은 학생에게 완료를 단정하지 않는다.
        Assert.Single(store.Receipts); Assert.Equal("synthetic", await File.ReadAllTextAsync(source));
    }
    [Fact]
    public async Task CancelMidTransferCleansPartialAndAllowsRetry()
    {
        Directory.CreateDirectory(_root); var source = Path.Combine(_root, "source.bin"); await File.WriteAllBytesAsync(source, new byte[100_000]);
        var session = Guid.NewGuid(); var student = Guid.NewGuid();
        using var store = new SubmissionStore(session, _root); store.SetOpen(true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool cancel = true; SubmissionClient client = null!;
        client = new(session, new CallbackChannel(frame =>
        {
            var message = CollaborationMessageCodec.Decode<SubmissionMessage>(frame, out _);
            var response = store.Handle(student, "학생", message);
            if (cancel && message.Action == SubmissionAction.Chunk) cancellation.Cancel();
            else client.Receive(response);
        }));
        client.Receive(new(session, Guid.Empty, SubmissionAction.State, IsOpen: true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SubmitAsync(source, token: cancellation.Token));
        Assert.Empty(store.Receipts); Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.partial"));
        cancel = false;
        await client.SubmitAsync(source).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(store.Receipts);
    }
    private sealed class CallbackChannel(Action<byte[]> send) : ICollaborationChannel
    {
        public Task SendAsync(byte[] frame, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); send(frame); return Task.CompletedTask; }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
