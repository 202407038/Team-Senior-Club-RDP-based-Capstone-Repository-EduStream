using EduStream.Core.Common;
using EduStream.Core.Models;
using EduStream.Core.FileSharing;
using EduStream.Server.Services;
using EduStream.Server.ViewModels;

namespace EduStream.FileTransfer.Tests;

public sealed class FeedbackNovemberTests
{
    [Fact]
    public void AnnouncementDisplayUsesLocalTimeWithoutChangingWireTimestamp()
    {
        var utc = new DateTimeOffset(2026, 11, 4, 0, 36, 0, TimeSpan.Zero);
        var item = new AnnouncementBanner().Receive(Guid.NewGuid(), "공지", false, utc);
        Assert.Equal(utc, item.SentAt);
        Assert.Equal(utc.ToLocalTime().Offset, item.LocalSentAt.Offset);
        Assert.Equal(utc.ToLocalTime().Hour, item.LocalSentAt.Hour);
        var korea = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        Assert.Equal("09:36", TimeZoneInfo.ConvertTime(item.SentAt, korea).ToString("HH:mm"));
    }

    [Theory]
    [InlineData("2024-001한글 A", "2024001")]
    [InlineData("００１١٢3", "3")]
    [InlineData("000123", "000123")]
    [InlineData("", "")]
    public void StudentNumberAllowsOnlyAsciiDigits(string input, string expected) => Assert.Equal(expected, StudentInputRules.StudentId(input));

    [Theory]
    [InlineData("가나다", "가나다")]
    [InlineData("가 나다123ABC", "가 나다ABC")]
    [InlineData("남궁가나다라마", "남궁가나다라마")]
    [InlineData("NGUYEN VAN AN", "NGUYEN VAN AN")]
    [InlineData("  김  민수A", "김 민수A")]
    [InlineData("Jean-Luc.K·Lee", "Jean-Luc.K·Lee")]
    [InlineData("가나다라마바사아자차카타파하가나다라마바사", "가나다라마바사아자차카타파하가나다라마바")]
    [InlineData("가나다", "가나다")]
    [InlineData("ㄱㅏ!", "")]
    [InlineData("ㄱㅏ!😀(복학)", "복학")]
    public void StudentNameAllowsRosterStyleNamesUpToTwentyCharacters(string input, string expected) => Assert.Equal(expected, StudentInputRules.Name(input));

    [Fact]
    public void LongForeignRosterNameCanJoinWhenTypedExactly()
    {
        var policy = new RosterAdmissionPolicy();
        policy.Configure([new(2, "NGUYEN VAN AN", "001", "", ""), new(3, "남궁가나다라마", "002", "", "")], true);
        foreach (var (typed, id) in new[] { ("NGUYEN VAN AN ", "001"), ("남궁가나다라마", "002") })
        {
            var name = StudentInputRules.Name(typed).Trim();
            Assert.True(policy.Allows(new() { DisplayName = RosterIdentity.ConnectionName(name, id), RosterName = name, StudentId = id }));
        }
        Assert.False(policy.Allows(new() { DisplayName = RosterIdentity.ConnectionName("NGUYEN VAN A", "001"), RosterName = "NGUYEN VAN A", StudentId = "001" }));
    }

    [Fact]
    public void TruncatedEmojiPasteDoesNotThrowDuringHangulNormalization()
    {
        Assert.Equal("가나다라", StudentInputRules.Name("가나다라" + (char)0xD83D));
        Assert.Equal("가나다", StudentInputRules.Name("가" + char.ConvertFromUtf32(0x1F600) + "나다"));
    }

    [Fact]
    public void PrivateHistoryUsesStudentIdentityButRoutesOnlyThroughCurrentConnection()
    {
        var book = new ChatConversations([]) { KeepHistoryAcrossSessions = true };
        var first = new DirectChatRecipient(Guid.NewGuid(), "가나다 (2024001)");
        var second = first with { ConnectionId = Guid.NewGuid() };
        var other = first with { Name = "가나다 (2024002)" };
        var conversation = book.GetOrAdd(first.HistoryKey, first.Name);
        conversation.ConnectionId = first.ConnectionId;
        conversation.Messages.Add(ChatLine.User(first.Name, "기록"));
        book.SetSession(Guid.NewGuid());
        var rejoined = book.GetOrAdd(second.HistoryKey, second.Name);
        rejoined.ConnectionId = second.ConnectionId;
        Assert.Same(conversation, rejoined);
        Assert.Single(rejoined.Messages);
        Assert.Equal(second.ConnectionId, rejoined.ConnectionId);
        Assert.NotSame(conversation, book.GetOrAdd(other.HistoryKey, other.Name));
        Assert.Equal(3, book.Items.Count);
        Assert.Single(new ChatConversations([]).Items); // 재실행은 메모리 기록을 복원하지 않음
    }

    [Fact]
    public void ClientHistoryScopeSeparatesTeacherAndStudent()
    {
        var book = new ChatConversations([]) { KeepHistoryAcrossSessions = true };
        book.SetScope("host-A/student-A");
        var chat = book.GetOrAdd("professor", "교수자");
        chat.Messages.Add(ChatLine.User("교수자", "개인"));
        book.SetScope("host-A/student-A"); book.SetSession(Guid.NewGuid());
        Assert.Same(chat, book.GetOrAdd("professor", "교수자"));
        book.SetScope("host-A/student-B");
        Assert.Single(book.Items);
    }

    [Fact]
    public void ClientRejoinKeepsChatButClearsOldSessionNotices()
    {
        var publicMessages = new System.Collections.ObjectModel.ObservableCollection<ChatLine>();
        var book = new ChatConversations(publicMessages) { KeepHistoryAcrossSessions = true, ResetNoticesOnNewSession = true };
        book.SetScope("host-A/student-A"); book.SetSession(Guid.NewGuid());
        Assert.Empty(publicMessages); // 첫 참가에는 구분선을 넣지 않음
        publicMessages.Add(ChatLine.User("교수자", "과제 안내"));
        publicMessages.Add(ChatLine.System("서버와의 연결이 끊어졌습니다."));
        var direct = book.GetOrAdd("professor", "교수자");
        direct.Messages.Add(ChatLine.User("교수자", "개인"));
        direct.Messages.Add(ChatLine.System("원격 제어가 끝났습니다."));

        book.SetScope("host-A/student-A"); book.SetSession(Guid.NewGuid());
        Assert.Equal(2, publicMessages.Count);
        Assert.Equal("과제 안내", publicMessages[0].Message);
        Assert.True(publicMessages[1].IsSystem);
        Assert.StartsWith("── 새 수업 참가", publicMessages[1].Message);
        Assert.Equal("개인", Assert.Single(direct.Messages).Message);

        book.SetScope("host-B/student-A");
        Assert.Empty(publicMessages); // 다른 교수자는 전체 대화도 섞지 않음
        Assert.Single(book.Items);

        var professorBook = new ChatConversations([]) { KeepHistoryAcrossSessions = true };
        professorBook.Items[0].Messages.Add(ChatLine.System("세션 열림"));
        professorBook.SetSession(Guid.NewGuid());
        Assert.Single(professorBook.Items[0].Messages); // 교수자 쪽 안내는 유지
    }

    [Theory]
    [InlineData("가나다 (2024001)", "2024001")]
    [InlineData("동명이인 (001)", "001")]
    public void SubmissionFolderIsStudentNumber(string name, string expected) => Assert.Equal(expected, SubmissionStore.StudentFolder(name));

    [Theory]
    [InlineData(@"\.\DISPLAY1", true, "모니터 1 (주 모니터) · 1920×1080")]
    [InlineData(@"\.\DISPLAY12", false, "모니터 12 · 1920×1080")]
    public void MonitorChoiceShowsFriendlyName(string device, bool primary, string expected) =>
        Assert.Equal(expected, new EduStream.ShareHost.MonitorInfo { DeviceName = device, IsPrimary = primary, Width = 1920, Height = 1080 }.Label);

    [Fact]
    public async Task ProfessorDropZoneShowsFileNameNotWholePath()
    {
        var vm = new ServerViewModel();
        var file = Path.Combine(Path.GetTempPath(), "EduStream-긴 폴더 이름-" + Guid.NewGuid().ToString("N"), "3주차 실습.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "x");
        try
        {
            vm.PrepareDroppedFile([file]);
            Assert.Equal("3주차 실습.pdf", vm.SelectedFileName);
            Assert.Equal("학생 파일 제출", vm.SubmissionHeader);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); await vm.ShutdownAsync(); }
    }

    [Fact]
    public void SubmissionKeepsOriginalNameAndNumbersDuplicates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "EduStream-SubmitName-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string Store(string text)
            {
                var partial = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".partial");
                File.WriteAllText(partial, text);
                return Path.GetFileName(SubmissionStore.MoveKeepingName(partial, dir, "1주차 과제.hwp"));
            }
            Assert.Equal("1주차 과제.hwp", Store("a"));
            Assert.Equal("1주차 과제 (2).hwp", Store("b"));
            Assert.Equal("1주차 과제 (3).hwp", Store("c"));
            Assert.Equal("a", File.ReadAllText(Path.Combine(dir, "1주차 과제.hwp"))); // 먼저 낸 파일은 덮어쓰지 않음
            Assert.Empty(Directory.GetFiles(dir, "*.partial"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task StudentSubmissionIsPreparedFirstAndSentOnlyByButton()
    {
        var vm = new EduStream.Client.ViewModels.ClientViewModel();
        var file = Path.Combine(Path.GetTempPath(), "EduStream-Prepare-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "x");
        try
        {
            Assert.False(vm.HasSelectedSubmission);
            vm.PrepareSubmissionFile([file, Path.GetTempPath()]);
            Assert.False(vm.HasSelectedSubmission); // 여러 개·폴더는 받지 않음
            vm.PrepareSubmissionFile([file]);
            Assert.Equal(Path.GetFileName(file), vm.SelectedSubmissionName);
            Assert.Empty(vm.SubmittedFiles); // 끌어 놓기만으로는 보내지 않음
            Assert.False(vm.SendSelectedSubmissionCommand.CanExecute(null)); // 제출이 열리지 않으면 보낼 수 없음
            Assert.True(vm.ClearSelectedSubmissionCommand.CanExecute(null));
            vm.ClearSelectedSubmissionCommand.Execute(null);
            Assert.False(vm.HasSelectedSubmission);
        }
        finally { File.Delete(file); await vm.ShutdownAsync(); }
    }

    [Fact]
    public void SubmissionFolderFallbackCannotEscapeRoot()
    {
        var key = SubmissionStore.StudentFolder("../ (../../outside)");
        Assert.StartsWith("미입력-", key);
        Assert.DoesNotContain("/", key); Assert.DoesNotContain("\\", key);
        Assert.NotEqual(key, SubmissionStore.StudentFolder("다른학생"));
    }

    [Fact]
    public async Task RegisteredRowPreventsDuplicateBroadcastAndUnregisterDuringSend()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var called = 0;
        var descriptor = new SessionFileDescriptor(Guid.NewGuid(), Guid.NewGuid(), 1, "자료.txt", 0, new string('A', 64), 4096);
        var item = new RegisteredFileItem(descriptor, _ => { }, "unused", async _ => { called++; await complete.Task; });
        item.SendAllCommand.Execute(null);
        Assert.True(item.IsSending); Assert.False(item.UnregisterCommand.CanExecute(null));
        item.SendAllCommand.Execute(null); Assert.Equal(1, called);
        complete.SetResult();
        for (var i = 0; i < 100 && item.IsSending; i++) await Task.Delay(10);
        Assert.False(item.IsSending); Assert.True(item.UnregisterCommand.CanExecute(null));
    }
}
