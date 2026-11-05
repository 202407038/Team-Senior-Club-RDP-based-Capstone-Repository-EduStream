using System.Security.Cryptography;
using EduStream.Core.FileSharing;
using EduStream.Core.Collaboration;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class SubmissionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EduStream-SubmissionTest-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void ClosedRejectedAndValidFilesStoredWithoutOverwriting()
    {
        using var store = new SubmissionStore(Guid.NewGuid(), _root);
        var student = Guid.NewGuid(); var data = new byte[] { 1, 2, 3 };
        var begin = Begin(store, data);
        Assert.Equal(SubmissionAction.Rejected, store.Handle(student, "학생 A", begin).Action);
        store.SetOpen(true);
        for (var i = 0; i < 2; i++)
        {
            begin = Begin(store, data);
            Assert.Equal(SubmissionAction.Ready, store.Handle(student, "학생 A", begin).Action);
            store.Handle(student, "학생 A", new(store.SessionId, begin.RequestId, SubmissionAction.Chunk, Content: data));
            Assert.Empty(store.Receipts.Skip(i));
            Assert.Equal(SubmissionAction.Stored, store.Handle(student, "학생 A", new(store.SessionId, begin.RequestId, SubmissionAction.Complete)).Action);
        }
        Assert.Equal(2, store.Receipts.Count);
        Assert.NotEqual(store.Receipts[0].LocalPath, store.Receipts[1].LocalPath);
        Assert.All(store.Receipts, x => Assert.Equal(data, File.ReadAllBytes(x.LocalPath)));
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.partial"));
    }
    [Fact]
    public void WrongConnectionHashAndCancelNeverProduceReceipt()
    {
        using var store = new SubmissionStore(Guid.NewGuid(), _root); store.SetOpen(true);
        var a = Guid.NewGuid(); var begin = Begin(store, [1]); store.Handle(a, "학생", begin);
        Assert.Equal(SubmissionAction.Rejected, store.Handle(Guid.NewGuid(), "위조", new(store.SessionId, begin.RequestId, SubmissionAction.Complete)).Action);
        store.Handle(a, "학생", new(store.SessionId, begin.RequestId, SubmissionAction.Chunk, Content: [2]));
        Assert.Equal(SubmissionAction.Rejected, store.Handle(a, "학생", new(store.SessionId, begin.RequestId, SubmissionAction.Complete)).Action);
        Assert.Empty(store.Receipts); Assert.Empty(Directory.GetFiles(store.DirectoryPath));
        begin = Begin(store, [1]); store.Handle(a, "학생", begin); store.SetOpen(false);
        Assert.Empty(Directory.GetFiles(store.DirectoryPath));
    }
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("CON")]
    public void PathNamesAreRejectedBeforeWriting(string name)
    {
        using var store = new SubmissionStore(Guid.NewGuid(), _root); store.SetOpen(true);
        Assert.Throws<CollaborationException>(() => store.Handle(Guid.NewGuid(), "학생", Begin(store, []) with { FileName = name }));
        Assert.False(Directory.Exists(store.DirectoryPath));
    }
    [Fact]
    public void EmptyFilesAndDisconnectRetryAreHandledWithoutPartialReceipts()
    {
        using var store = new SubmissionStore(Guid.NewGuid(), _root); store.SetOpen(true);
        var student = Guid.NewGuid(); var begin = Begin(store, [1, 2]);
        store.Handle(student, "학생", begin);
        store.Handle(student, "학생", new(store.SessionId, begin.RequestId, SubmissionAction.Chunk, Content: [1]));
        store.Cancel(student); // 실제 연결 제거 경로와 같은 정리
        Assert.Empty(store.Receipts); Assert.Empty(Directory.GetFiles(store.DirectoryPath));
        Assert.Equal(SubmissionAction.Rejected, store.Handle(student, "학생", new(store.SessionId, begin.RequestId, SubmissionAction.Complete)).Action);
        begin = Begin(store, []); store.Handle(student, "학생", begin);
        Assert.Equal(SubmissionAction.Stored, store.Handle(student, "학생", new(store.SessionId, begin.RequestId, SubmissionAction.Complete)).Action);
        Assert.Empty(File.ReadAllBytes(Assert.Single(store.Receipts).LocalPath));
    }
    [Fact]
    public void OutOfOrderChunksAndDifferentSessionsDoNotComplete()
    {
        using var store = new SubmissionStore(Guid.NewGuid(), _root); store.SetOpen(true);
        var student = Guid.NewGuid(); var begin = Begin(store, [1]);
        Assert.Equal(SubmissionAction.Rejected, store.Handle(student, "학생", begin with { SessionId = Guid.NewGuid() }).Action);
        store.Handle(student, "학생", begin);
        Assert.Equal(SubmissionAction.Rejected, store.Handle(student, "학생", new(store.SessionId, begin.RequestId, SubmissionAction.Chunk, Index: 1, Content: [1])).Action);
        Assert.Empty(store.Receipts); Assert.Empty(Directory.GetFiles(store.DirectoryPath));
    }
    [Fact]
    public void CapacityAndDuplicateRequestRejectionsPreserveOtherTransfers()
    {
        using var store = new SubmissionStore(Guid.NewGuid(), _root); store.SetOpen(true);
        var students = Enumerable.Range(0, SessionFileLimits.MaxConcurrentTransfers).Select(_ => Guid.NewGuid()).ToArray();
        var requests = students.Select(_ => Begin(store, [1])).ToArray();
        for (var i = 0; i < students.Length; i++)
            Assert.Equal(SubmissionAction.Ready, store.Handle(students[i], "학생 " + i, requests[i]).Action);
        Assert.Equal(SubmissionAction.Rejected, store.Handle(students[0], "학생 0", Begin(store, [2])).Action);
        var extra = Guid.NewGuid(); var extraRequest = Begin(store, [3]);
        Assert.Equal(SubmissionAction.Rejected, store.Handle(extra, "추가 학생", extraRequest).Action);
        Assert.Equal(students.Length, Directory.GetFiles(store.DirectoryPath, "*.partial").Length);
        store.Cancel(students[0]);
        Assert.Equal(SubmissionAction.Ready, store.Handle(extra, "추가 학생", extraRequest).Action);
        for (var i = 1; i < students.Length; i++)
        {
            store.Handle(students[i], "학생 " + i, new(store.SessionId, requests[i].RequestId, SubmissionAction.Chunk, Content: [1]));
            Assert.Equal(SubmissionAction.Stored, store.Handle(students[i], "학생 " + i, new(store.SessionId, requests[i].RequestId, SubmissionAction.Complete)).Action);
        }
        Assert.Equal(students.Length - 1, store.Receipts.Count);
        store.SetOpen(false);
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.partial"));
        Assert.All(store.Receipts, receipt => Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(receipt.LocalPath)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrExcessBytesAreRejectedWithoutReceipt(bool excess)
    {
        using var store = new SubmissionStore(Guid.NewGuid(), _root); store.SetOpen(true);
        var student = Guid.NewGuid(); var request = Begin(store, [1]);
        store.Handle(student, "학생", request);
        var response = excess
            ? store.Handle(student, "학생", new(store.SessionId, request.RequestId, SubmissionAction.Chunk, Content: [1, 2]))
            : store.Handle(student, "학생", new(store.SessionId, request.RequestId, SubmissionAction.Complete));
        Assert.Equal(SubmissionAction.Rejected, response.Action);
        Assert.Empty(store.Receipts);
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.partial"));
    }
    [Fact]
    public void UnwritableDestinationIsRejectedAndCanRetryAfterRepair()
    {
        using var store = new SubmissionStore(Guid.NewGuid(), _root); store.SetOpen(true);
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        // 동일 경로의 일반 파일로 디렉터리 생성을 막는다. 사용자 ACL/디스크 상태는 바꾸지 않는다.
        File.WriteAllText(store.DirectoryPath, "synthetic blocker");
        var student = Guid.NewGuid(); var request = Begin(store, []);
        Assert.Equal(SubmissionAction.Rejected, store.Handle(student, "학생", request).Action);
        Assert.Empty(store.Receipts);
        Assert.Equal("synthetic blocker", File.ReadAllText(store.DirectoryPath));
        File.Delete(store.DirectoryPath);
        Assert.Equal(SubmissionAction.Ready, store.Handle(student, "학생", request).Action);
        Assert.Equal(SubmissionAction.Stored, store.Handle(student, "학생", new(store.SessionId, request.RequestId, SubmissionAction.Complete)).Action);
        Assert.Single(store.Receipts);
    }
    private static SubmissionMessage Begin(SubmissionStore store, byte[] bytes) => new(store.SessionId, Guid.NewGuid(), SubmissionAction.Begin,
        "과제.txt", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
