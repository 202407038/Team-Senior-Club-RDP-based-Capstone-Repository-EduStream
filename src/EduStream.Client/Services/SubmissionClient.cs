using System.IO;
using System.Security.Cryptography;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;

namespace EduStream.Client.Services;

public sealed class SubmissionClient(Guid sessionId, ICollaborationChannel channel)
{
    private readonly object _gate = new();
    private TaskCompletionSource<SubmissionMessage>? _waiting;
    private Guid _request;
    private bool _busy;
    private bool _closed;
    public bool IsOpen { get; private set; }
    public event Action? Changed;
    public Task QueryAsync() => Send(new(sessionId, Guid.Empty, SubmissionAction.Query), CancellationToken.None);
    public void Receive(SubmissionMessage message)
    {
        message.Validate();
        if (message.SessionId != sessionId) return;
        lock (_gate)
        {
            if (_closed) return;
            if (message.Action == SubmissionAction.State)
            {
                IsOpen = message.IsOpen;
                if (!IsOpen) _waiting?.TrySetException(new IOException("교수자가 제출을 닫았습니다."));
            }
            else if (message.RequestId == _request && message.Action is SubmissionAction.Ready or SubmissionAction.Stored or SubmissionAction.Rejected)
                _waiting?.TrySetResult(message);
        }
        Changed?.Invoke();
    }
    public async Task SubmitAsync(string path, IProgress<int>? progress = null, CancellationToken token = default)
    {
        lock (_gate)
        {
            if (_busy || _closed || !IsOpen) throw new InvalidOperationException("현재 제출할 수 없습니다.");
            _busy = true; _request = Guid.NewGuid();
        }
        try
        {
            var name = Path.GetFileName(path); SessionFileLimits.ValidateName(name);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, SubmissionMessage.ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length > SessionFileLimits.MaxFileBytes) throw new IOException("파일당 최대 512 MiB까지 제출할 수 있습니다.");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token)); input.Position = 0;
            await Exchange(new(sessionId, _request, SubmissionAction.Begin, name, input.Length, hash), SubmissionAction.Ready, token);
            var buffer = new byte[SubmissionMessage.ChunkBytes]; long sent = 0; var index = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                await Exchange(new(sessionId, _request, SubmissionAction.Chunk, Index: index++, Content: buffer[..count]), SubmissionAction.Ready, token);
                sent += count; progress?.Report(input.Length == 0 ? 100 : (int)(sent * 100 / input.Length));
            }
            await Exchange(new(sessionId, _request, SubmissionAction.Complete), SubmissionAction.Stored, token);
            progress?.Report(100);
        }
        catch
        {
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await Send(new(sessionId, _request, SubmissionAction.Cancel), timeout.Token); } catch { }
            throw;
        }
        finally { lock (_gate) { _busy = false; _waiting = null; _request = Guid.Empty; } }
    }
    private async Task Exchange(SubmissionMessage message, SubmissionAction expected, CancellationToken token)
    {
        TaskCompletionSource<SubmissionMessage> response;
        lock (_gate)
        {
            if (_closed || !IsOpen) throw new IOException("제출 연결이 종료되었습니다.");
            response = _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await Send(message, timeout.Token);
        var result = await response.Task.WaitAsync(timeout.Token);
        if (result.Action != expected) throw new IOException(result.Detail.Length > 0 ? result.Detail : "교수자 저장 확인을 받지 못했습니다.");
    }
    private Task Send(SubmissionMessage message, CancellationToken token) => channel.SendAsync(CollaborationMessageCodec.Encode(Guid.NewGuid(), message), token);
    public void ConnectionClosed()
    {
        lock (_gate) { _closed = true; IsOpen = false; _waiting?.TrySetException(new IOException("연결이 종료되어 제출 확인을 받지 못했습니다.")); }
        Changed?.Invoke();
    }
}
