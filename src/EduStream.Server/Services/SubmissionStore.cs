using System.IO;
using System.Security.Cryptography;
using System.Text;
using EduStream.Core.FileSharing;

namespace EduStream.Server.Services;

public sealed record SubmissionReceipt(string Student, string FileName, string LocalPath, long Length, string Sha256, DateTimeOffset At)
{
    public string Summary => $"{Student} · {FileName} · {At:MM-dd HH:mm:ss} · {Length:N0} byte";
}

/// <summary>학생 파일을 요청별 임시 파일에 순차 저장하고 길이·해시가 맞을 때만 확정한다.</summary>
public sealed class SubmissionStore : IDisposable
{
    private sealed class Incoming(string student, SubmissionMessage begin, string path) : IDisposable
    {
        public string Student { get; } = student;
        public SubmissionMessage Begin { get; } = begin;
        public string Path { get; } = path;
        public FileStream Output { get; } = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Received; public int Index;
        public void Dispose() { Output.Dispose(); Hash.Dispose(); if (File.Exists(Path)) File.Delete(Path); }
    }
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Incoming> _incoming = new();
    private readonly List<SubmissionReceipt> _receipts = [];
    private bool _open;
    private bool _disposed;
    public Guid SessionId { get; }
    public string DirectoryPath { get; }
    public bool IsOpen { get { lock (_gate) return _open; } }
    public IReadOnlyList<SubmissionReceipt> Receipts { get { lock (_gate) return _receipts.ToArray(); } }
    public event Action? Changed;
    public SubmissionStore(Guid sessionId, string root)
    {
        SessionId = sessionId;
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("절대 저장 경로가 필요합니다.");
        DirectoryPath = Path.GetFullPath(root);
    }
    public void SetOpen(bool open)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _open = open;
            if (!open) { foreach (var item in _incoming.Values) item.Dispose(); _incoming.Clear(); }
        }
        Changed?.Invoke();
    }
    public SubmissionMessage Handle(Guid connectionId, string verifiedName, SubmissionMessage message)
    {
        message.Validate();
        lock (_gate)
        {
            if (_disposed || message.SessionId != SessionId) return Reply(message, SubmissionAction.Rejected, "종료되었거나 다른 세션입니다.");
            if (message.Action == SubmissionAction.Query) return new(SessionId, Guid.Empty, SubmissionAction.State, IsOpen: _open);
            if (message.Action == SubmissionAction.Cancel)
            {
                if (_incoming.TryGetValue(connectionId, out var cancelled) && cancelled.Begin.RequestId == message.RequestId) Cancel(connectionId);
                return Reply(message, SubmissionAction.Rejected, "제출 취소됨");
            }
            if (!_open) return Reply(message, SubmissionAction.Rejected, "교수자가 제출을 열지 않았습니다.");
            try
            {
                if (message.Action == SubmissionAction.Begin)
                {
                    if (_incoming.ContainsKey(connectionId) || _incoming.Count >= SessionFileLimits.MaxConcurrentTransfers)
                        return Reply(message, SubmissionAction.Rejected, "진행 중인 제출이 있습니다. 잠시 후 다시 시도해 주세요.");
                    Directory.CreateDirectory(DirectoryPath);
                    _incoming.Add(connectionId, new Incoming(verifiedName, message, Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".partial")));
                    return Reply(message, SubmissionAction.Ready);
                }
                if (!_incoming.TryGetValue(connectionId, out var item) || item.Begin.RequestId != message.RequestId)
                    return Reply(message, SubmissionAction.Rejected, "유효한 제출 요청이 없습니다.");
                if (message.Action == SubmissionAction.Chunk)
                {
                    if (message.Index != item.Index || message.Content is not { Length: > 0 } data || item.Received + data.Length > item.Begin.Length)
                        throw new InvalidDataException();
                    item.Output.Write(data); item.Hash.AppendData(data); item.Received += data.Length; item.Index++;
                    return Reply(message, SubmissionAction.Ready);
                }
                if (message.Action != SubmissionAction.Complete || item.Received != item.Begin.Length ||
                    !Convert.ToHexString(item.Hash.GetHashAndReset()).Equals(item.Begin.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
                item.Output.Flush(true); item.Output.Dispose();
                var targetDirectory = Path.Combine(DirectoryPath, StudentFolder(item.Student));
                Directory.CreateDirectory(targetDirectory);
                var target = Path.Combine(targetDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{item.Begin.FileName}");
                File.Move(item.Path, target, false);
                _receipts.Add(new(item.Student, item.Begin.FileName, target, item.Begin.Length, item.Begin.Sha256, DateTimeOffset.Now));
                _incoming.Remove(connectionId); item.Dispose();
                Changed?.Invoke();
                return Reply(message, SubmissionAction.Stored);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Cancel(connectionId);
                return Reply(message, SubmissionAction.Rejected, "저장 또는 파일 검증에 실패했습니다. 다시 제출해 주세요.");
            }
        }
    }
    private SubmissionMessage Reply(SubmissionMessage request, SubmissionAction action, string detail = "") => new(SessionId, request.RequestId, action, Detail: detail);
    public static string StudentFolder(string verifiedName)
    {
        var start = verifiedName.LastIndexOf(" (", StringComparison.Ordinal);
        if (start >= 0 && verifiedName.EndsWith(')'))
        {
            var id = verifiedName[(start + 2)..^1];
            if (id.Length is > 0 and <= 32 && id.All(c => c is >= '0' and <= '9')) return id;
        }
        // 학번 없는 기존 참가도 허용. 이름을 경로로 직접 쓰거나 경로 이탈 문자를 허용하지 않는다.
        return "미입력-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(verifiedName)))[..12];
    }
    public void Cancel(Guid connectionId) { lock (_gate) { if (_incoming.Remove(connectionId, out var item)) item.Dispose(); } }
    public void Dispose() { lock (_gate) { if (_disposed) return; SetOpen(false); _disposed = true; } }
}
