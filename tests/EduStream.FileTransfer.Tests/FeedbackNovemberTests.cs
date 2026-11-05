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
    [InlineData("가 나다123ABC", "가나다")]
    [InlineData("가나다라마바사", "가나다라마")]
    [InlineData("가나다", "가나다")]
    [InlineData("ㄱㅏ!", "")]
    public void StudentNameAllowsFiveComposedHangulSyllables(string input, string expected) => Assert.Equal(expected, StudentInputRules.Name(input));

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

    [Theory]
    [InlineData("가나다 (2024001)", "2024001")]
    [InlineData("동명이인 (001)", "001")]
    public void SubmissionFolderIsStudentNumber(string name, string expected) => Assert.Equal(expected, SubmissionStore.StudentFolder(name));

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
