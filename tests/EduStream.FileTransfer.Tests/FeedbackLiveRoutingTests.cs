using System.Reflection;
using EduStream.Client.ViewModels;
using EduStream.Core.Network;
using EduStream.Server.ViewModels;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>창/공유 엔진 없이 실제 루프백 연결로 변경된 화면 명령의 끝단까지 확인.</summary>
public sealed class FeedbackLiveRoutingTests
{
    [Fact(Timeout = 60000)]
    public async Task RejoinKeepsOnePrivateThread_SeparateAnnouncementAndRowBroadcastReachStudents()
    {
        var root = Path.Combine(Path.GetTempPath(), "EduStream-feedback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var certificate = ProfessorCertificateStore.CreateEphemeral();
        var server = new ServerViewModel();
        var port = TestPortAllocator.GetFreePortPair();
        var students = Enumerable.Range(1, 2).Select(i => new ClientViewModel(downloadsDirectory: new ReadinessDirectory(Path.Combine(root, "받은파일" + i)))
        { HostAddress = "127.0.0.1", Port = port, DisplayName = "김학생", StudentId = "00" + i }).ToArray();
        try
        {
            await server.SessionManager.OpenSessionAsync("Feedback", port, ReadOnlyMemory<char>.Empty, certificate);
            typeof(ServerViewModel).GetProperty(nameof(ServerViewModel.IsSessionOpen))!.SetValue(server, true);
            foreach (var student in students) { student.JoinSessionCommand.Execute(null); await Until(() => student.IsConnected && !student.IsConnecting); }
            await Until(() => server.Conversations.Items.Count == 3 && server.ParticipantCount == 2);
            Assert.Empty(students[0].ChatMessages); Assert.Empty(students[1].ChatMessages);
            Assert.Equal(2, server.AttendanceRows.Count);
            var first = server.Conversations.Items.Single(x => x.Name == "김학생 (001)");
            students[0].Conversations.Selected = students[0].Conversations.GetOrAdd("professor", "교수자");
            students[0].ChatInput = "재접속 전 질문"; students[0].SendChatCommand.Execute(null);
            await Until(() => first.Messages.Count == 1);
            var oldConnection = first.ConnectionId;
            var studentHistory = students[0].Conversations.Selected;
            students[0].DisconnectCommand.Execute(null);
            await Until(() => !students[0].IsConnected && !students[0].IsConnecting && !first.IsAvailable);
            Assert.Null(first.ConnectionId);
            server.Conversations.Selected = first; server.ChatInput = "아직 보내면 안 됨"; server.SendChatCommand.Execute(null);
            Assert.True(server.IsStatusError); Assert.Single(first.Messages);
            students[0].JoinSessionCommand.Execute(null);
            await Until(() => students[0].IsConnected && !students[0].IsConnecting && first.IsAvailable);
            Assert.Equal(3, server.Conversations.Items.Count);
            Assert.Same(first, server.Conversations.Items.Single(x => x.Name == "김학생 (001)"));
            Assert.NotEqual(oldConnection, first.ConnectionId);
            Assert.Same(studentHistory, students[0].Conversations.GetOrAdd("professor", "교수자"));
            server.ChatInput = "재접속 후 답변"; server.SendChatCommand.Execute(null);
            await Until(() => studentHistory.Messages.Count == 2);
            Assert.Empty(students[1].Conversations.GetOrAdd("professor", "교수자").Messages);

            // 개인 채팅 선택·작성 내용과 독립된 공지 작성/전송.
            server.ChatInput = "개인 답변 초안";
            server.ComposeImportantAnnouncementCommand.Execute(null);
            server.AnnouncementInput = "중요 안내"; server.PublishAnnouncementCommand.Execute(null);
            await Until(() => !server.IsAnnouncementComposerOpen && students.All(s => s.Announcement.Items.Count == 1));
            Assert.Equal("개인 답변 초안", server.ChatInput); Assert.Same(first, server.Conversations.Selected);
            Assert.All(students, s => Assert.True(s.Announcement.Items[0].IsImportant));
            server.ComposeAnnouncementCommand.Execute(null);
            server.AnnouncementInput = "일반 안내"; server.PublishAnnouncementCommand.Execute(null);
            await Until(() => students.All(s => s.Announcement.Items.Count == 2));
            Assert.All(students, s => Assert.False(s.Announcement.Items[0].IsImportant));

            var source = Path.Combine(root, "강의자료.bin"); var bytes = new byte[140_005]; Random.Shared.NextBytes(bytes);
            await File.WriteAllBytesAsync(source, bytes);
            server.SelectedFilePath = source; server.RegisterSelectedFileCommand.Execute(null);
            await Until(() => server.RegisteredFiles.Count == 1 && students.All(s => s.SessionFiles.Count == 1));
            var file = server.RegisteredFiles.Single(); file.SendAllCommand.Execute(null);
            await Until(() => !file.IsSending && students.All(s => s.ReceivedFiles.Count == 1));
            foreach (var student in students) Assert.Equal(bytes, await File.ReadAllBytesAsync(student.ReceivedFiles[0].LocalPath));
            // 같은 이름으로 원본을 수정하면 등록된 자료로 가장하여 보내지 않음.
            await File.WriteAllTextAsync(source, "수정된 원본");
            file.SendAllCommand.Execute(null); await Until(() => !file.IsSending);
            Assert.Contains("원본이 변경", server.FileShareStatus);
            Assert.All(students, s => Assert.Single(s.ReceivedFiles));
            file.UnregisterCommand.Execute(null);
            await Until(() => students.All(s => s.SessionFiles.Count == 0));
            Assert.True(File.Exists(source));
            Assert.All(students, s => Assert.True(File.Exists(s.ReceivedFiles[0].LocalPath)));
        }
        finally
        {
            foreach (var student in students) await student.ShutdownAsync();
            await server.ShutdownAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(20); }
    }
}
