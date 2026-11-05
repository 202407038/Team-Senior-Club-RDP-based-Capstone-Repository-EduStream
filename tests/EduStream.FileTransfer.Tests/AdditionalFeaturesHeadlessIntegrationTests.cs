using System.Collections.Concurrent;
using System.Reflection;
using EduStream.Client.Services;
using EduStream.Client.ViewModels;
using EduStream.Core.Factories;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Network;
using EduStream.Core.Serialization;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

/// <summary>실제 루프백 TLS/TCP와 임시 디스크. 창, WDS, 화면 캡처, 입력 주입은 사용하지 않는다.</summary>
public sealed class AdditionalFeaturesHeadlessIntegrationTests
{
    [Fact(Timeout = 60000)]
    public async Task ThreeStudents_DirectChatAnnouncementsDownloadsAndSubmissionsStaySeparated()
    {
        var root = Path.Combine(Path.GetTempPath(), "EduStream-headless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var certificate = ProfessorCertificateStore.CreateEphemeral();
        var log = new InMemoryLogSink();
        var manager = new SessionManager(log, new TcpServerService(log, new PacketSerializer()), Path.Combine(root, "submissions"));
        manager.ConfigureRosterAdmission(Enumerable.Range(1, 3).Select(i => new RosterRow(i + 1, "동명이인", $"00{i}", "", "")).ToArray(), true);
        var port = TestPortAllocator.GetFreePortPair();
        var clients = Enumerable.Range(1, 3).Select(i => new ClientViewModel(downloadsDirectory: new ReadinessDirectory(Path.Combine(root, "downloads-" + i)))
        { HostAddress = "127.0.0.1", Port = port, DisplayName = "동명이인", StudentId = $"00{i}" }).ToArray();
        var publicMessages = clients.Select(c => Observe(c.ChatMessages)).ToArray();
        try
        {
            var session = await manager.OpenSessionAsync("Headless integration", port, ReadOnlyMemory<char>.Empty, certificate);
            foreach (var client in clients)
            {
                client.JoinSessionCommand.Execute(null);
                await Until(() => !client.IsConnecting && client.IsConnected, "학생 참가");
            }
            await Until(() => manager.ParticipantCount == 3 && manager.DirectChatRecipients.Count == 3, "명단 연결");
            Assert.Equal(3, manager.Attendance.Presence.Count(x => x.Connected));

            clients[0].ChatInput = "동명이인 공개 채팅";
            clients[0].SendChatCommand.Execute(null);
            await Until(() => publicMessages.All(messages => messages.Contains("동명이인 공개 채팅")), "공개 채팅 수신");
            var echoes = clients.Select(client => client.ChatMessages.Single(line => line.Message == "동명이인 공개 채팅")).ToArray();
            Assert.All(echoes, line => Assert.Equal("동명이인", line.Sender));
            Assert.True(echoes[0].IsSelf);
            Assert.All(echoes.Skip(1), line => Assert.False(line.IsSelf));

            var privateReceived = new ConcurrentQueue<DirectChatPacket>();
            var privateMessages = new ConcurrentQueue<string>[clients.Length];
            manager.DirectChatReceived += privateReceived.Enqueue;
            for (var i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                client.Conversations.Selected = client.Conversations.GetOrAdd("professor", "교수자");
                privateMessages[i] = Observe(client.Conversations.Selected.Messages);
                client.ChatInput = "개인 질문 " + i; client.SendChatCommand.Execute(null);
            }
            await Until(() => privateReceived.Count == 3, "학생 개인 채팅");
            foreach (var received in privateReceived)
                await manager.SendDirectChatAsync(received.StudentConnectionId, "답변:" + received.Sender);
            for (var i = 0; i < clients.Length; i++)
            {
                var client = clients[i]; var reply = $"답변:동명이인 (00{i + 1})";
                await Until(() => privateMessages[i].Contains(reply), "개인 답변");
                Assert.Single(privateMessages[i].Where(x => x.StartsWith("답변:")));
                Assert.DoesNotContain(publicMessages[i], x => x.StartsWith("개인 질문") || x.StartsWith("답변:"));
            }

            var notice = PacketFactory.CreateChat("Server", "교수자", "중요 합성 공지", session.SessionId);
            notice.IsAnnouncement = true; notice.IsImportantAnnouncement = true;
            await manager.BroadcastPacketAsync(notice);
            await Until(() => manager.Announcements.Snapshot().Single().Received == 3, "공지 수신 확인");
            Assert.Equal(0, manager.Announcements.Snapshot().Single().Confirmed);
            clients[0].Announcement.Items.Single().ConfirmCommand.Execute(null);
            await Until(() => manager.Announcements.Snapshot().Single().Confirmed == 1, "명시적 공지 확인");
            clients[1].Announcement.Items.Single().DismissCommand.Execute(null);
            Assert.Equal(1, manager.Announcements.Snapshot().Single().Confirmed);
            Assert.Single(clients[1].Announcement.Items);

            var source = Path.Combine(root, "한글 과제.bin");
            var bytes = Enumerable.Range(0, 160_123).Select(i => (byte)(i % 251)).ToArray();
            await File.WriteAllBytesAsync(source, bytes);
            var file = await manager.RegisterFileAsync(source);
            await manager.SetSubmissionsOpenAsync(true);
            await Until(() => clients.All(c => c.SessionFiles.Count == 1 && Submission(c).IsOpen), "파일 목록과 제출 열림");
            var uploads = clients.Select(c => c.SubmitPathAsync(source)).ToArray();
            foreach (var client in clients) client.SessionFiles.Single().DownloadCommand.Execute(null);
            await Task.WhenAll(uploads).WaitAsync(TimeSpan.FromSeconds(15));
            await Until(() => manager.FileTransfers!.DeliveryStatus.Count(x => x.Status == "저장 완료") == 3, "다운로드 저장 확인");
            Assert.Equal(3, manager.Submissions!.Receipts.Count);
            Assert.All(clients, c => { Assert.Single(c.SubmittedFiles); Assert.Empty(c.SubmissionStatus); });
            Assert.Equal(3, manager.Submissions.Receipts.Select(x => Path.GetDirectoryName(x.LocalPath)).Distinct().Count());
            foreach (var receipt in manager.Submissions.Receipts)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(receipt.LocalPath));
                Assert.Equal(SubmissionStore.StudentFolder(receipt.Student), Path.GetFileName(Path.GetDirectoryName(receipt.LocalPath)));
            }
            for (var i = 0; i < clients.Length; i++)
            {
                var expected = Path.Combine(root, "downloads-" + (i + 1), file.FileName);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(expected));
                await Until(() => clients[i].ReceivedFiles.Count == 1, "학생 저장 목록");
                Assert.Equal(expected, clients[i].ReceivedFiles.Single().LocalPath);
            }

            Assert.True(manager.UnregisterFile(file.FileId));
            await Until(() => clients.All(c => c.SessionFiles.Count == 0), "강의 파일 해제");
            Assert.True(File.Exists(source));
            Assert.All(clients, c => Assert.True(File.Exists(c.ReceivedFiles.Single().LocalPath)));
            await manager.SetSubmissionsOpenAsync(false);
            await Until(() => clients.All(c => !Submission(c).IsOpen), "제출 닫힘");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Submission(clients[0]).SubmitAsync(source));
            var oldRecipient = manager.DirectChatRecipients.Single(x => x.Name == "동명이인 (001)").ConnectionId;
            clients[0].DisconnectCommand.Execute(null);
            await Until(() => manager.ParticipantCount == 2 && !clients[0].IsConnected, "학생 이탈");
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SendDirectChatAsync(oldRecipient, "이탈한 학생"));
            Assert.False(manager.Attendance.Presence.Single(x => x.StudentId == "001").Connected);
            Assert.All(manager.Attendance.Presence.Where(x => x.StudentId != "001"), x => Assert.True(x.Connected));
            Assert.Contains("001", manager.Attendance.ToCsv());
        }
        finally
        {
            foreach (var client in clients) await client.ShutdownAsync();
            await manager.CloseSessionAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static SubmissionClient Submission(ClientViewModel vm) => (SubmissionClient)typeof(ClientViewModel)
        .GetField("_submissionClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
    // 실제 앱은 UI Dispatcher에서 읽는다. 비화면 검사에서는 수신 중인 UI 목록을 병렬 열거하지 않는다.
    private static ConcurrentQueue<string> Observe(System.Collections.ObjectModel.ObservableCollection<ChatLine> messages)
    {
        var observed = new ConcurrentQueue<string>();
        messages.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is null) return;
            foreach (ChatLine line in args.NewItems) observed.Enqueue(line.Message);
        };
        return observed;
    }
    private static async Task Until(Func<bool> condition, string step)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(step);
            await Task.Delay(20);
        }
    }
}
