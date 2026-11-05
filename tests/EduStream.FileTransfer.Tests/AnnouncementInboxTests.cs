using EduStream.Core.Common;

namespace EduStream.FileTransfer.Tests;

public class AnnouncementInboxTests
{
    [Fact]
    public void ConfirmClosesOnlyThatImportantNoticeAndKeepsHistoryAndRetry()
    {
        var inbox = new AnnouncementBanner();
        var first = inbox.Receive(Guid.NewGuid(), "중요 1", true, DateTimeOffset.UtcNow);
        var second = inbox.Receive(Guid.NewGuid(), "중요 2", true, DateTimeOffset.UtcNow);
        var confirmations = 0;
        inbox.Confirmed += item => { Assert.Same(first, item); confirmations++; };

        first.ConfirmCommand.Execute(null);

        Assert.True(first.IsRead);
        Assert.True(first.IsDismissed);
        Assert.Same(second, Assert.Single(inbox.ImportantItems));
        Assert.Contains(first, inbox.Items);
        Assert.Equal(1, inbox.UnreadCount);
        Assert.Equal(1, confirmations);
        // 공지함에서 다시 확인하여 전송을 재시도할 수 있다.
        first.ConfirmCommand.Execute(null);
        Assert.Equal(2, confirmations);
        Assert.Single(inbox.ImportantItems);
    }

    [Fact]
    public void ImportantNoticesSurviveExpiryAndDismissDoesNotMeanRead()
    {
        var clock = new Clock(); var inbox = new AnnouncementBanner(clock);
        var first = inbox.Receive(Guid.NewGuid(), "중요 1", true, clock.GetUtcNow());
        inbox.Receive(Guid.NewGuid(), "중요 2", true, clock.GetUtcNow());
        inbox.Receive(Guid.NewGuid(), "일반", false, clock.GetUtcNow());
        clock.Now = clock.Now.AddSeconds(3); inbox.Refresh();
        Assert.False(inbox.IsVisible); Assert.Equal(2, inbox.ImportantItems.Count);
        first.DismissCommand.Execute(null);
        Assert.Single(inbox.ImportantItems); Assert.Equal(3, inbox.Items.Count);
        Assert.False(first.IsRead); Assert.Equal(3, inbox.UnreadCount);
        first.ConfirmCommand.Execute(null);
        Assert.True(first.IsImportant); Assert.Equal(2, inbox.UnreadCount);
    }

    [Fact]
    public void ReplayKeepsDismissedReadStateAndNewSessionClearsHistory()
    {
        var inbox = new AnnouncementBanner(); var session = Guid.NewGuid(); inbox.SetSession(session);
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var item = inbox.Receive(id, "중요", true, now);
        item.DismissCommand.Execute(null); item.ConfirmCommand.Execute(null);
        inbox.SetSession(session); inbox.Receive(id, "중요", true, now, history:true);
        Assert.Single(inbox.Items); Assert.Empty(inbox.ImportantItems); Assert.Equal(0, inbox.UnreadCount);
        inbox.IsOpen = true; inbox.SetSession(Guid.NewGuid());
        Assert.Empty(inbox.Items); Assert.False(inbox.IsOpen);
    }

    [Fact]
    public void OpeningInboxAndHistoryDoNotAutomaticallyConfirmOrFlash()
    {
        var inbox = new AnnouncementBanner(); var confirmations = 0;
        inbox.Confirmed += _ => confirmations++;
        var item = inbox.Receive(Guid.NewGuid(), "이전 일반", false, DateTimeOffset.UtcNow, history:true);
        inbox.IsOpen = true;
        Assert.Equal(1, inbox.UnreadCount); Assert.False(inbox.IsVisible); Assert.Equal(0, confirmations);
        item.ConfirmCommand.Execute(null); Assert.Equal(1, confirmations);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
