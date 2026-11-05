using System.Reflection;
using EduStream.Client.ViewModels;
using EduStream.Core.Common;
using EduStream.Core.FileSharing;
using EduStream.Core.Models;
using EduStream.Core.Serialization;

namespace EduStream.FileTransfer.Tests;

public sealed class UsabilityChatTests
{
    [Fact]
    public void ReadingHistoryDoesNotFollowNewMessagesUntilExplicitBottom()
    {
        var state = new ChatReadingState();
        state.Receive();
        Assert.True(state.ShouldFollow);
        state.ViewPosition(false);
        state.Receive(3);
        Assert.False(state.ShouldFollow);
        Assert.Equal(3, state.UnreadCount);
        state.IsVisible = false;
        state.Receive();
        state.IsVisible = true;
        Assert.Equal(4, state.UnreadCount);
        Assert.False(state.ShouldFollow);
        state.ViewPosition(true);
        Assert.False(state.HasUnread);
        Assert.True(state.ShouldFollow);
    }

    [Fact]
    public void HiddenBottomDoesNotBecomeReadJustByShowingPanel()
    {
        var state = new ChatReadingState { IsVisible = false };
        state.Receive(2);
        state.ViewPosition(true);
        Assert.Equal(2, state.UnreadCount);
        state.IsVisible = true;
        Assert.False(state.ShouldFollow);
        state.ViewPosition(true);
        Assert.Equal(0, state.UnreadCount);
    }

    [Fact]
    public void AnnouncementExpiryRestartsForNewNoticeAndCanBeDismissed()
    {
        var clock = new ManualClock();
        var banner = new AnnouncementBanner(clock);
        banner.Show("첫 공지");
        clock.Advance(2);
        banner.Refresh();
        Assert.True(banner.IsVisible);
        banner.Show("두 번째 공지");
        clock.Advance(2);
        banner.Refresh();
        Assert.Equal("두 번째 공지", banner.Text);
        clock.Advance(1);
        banner.Refresh();
        Assert.False(banner.IsVisible);
        banner.Show("공지");
        banner.Dismiss();
        Assert.False(banner.IsVisible);
    }

    [Fact]
    public async Task ClientKeepsAnnouncementInHistoryAndCountsHiddenChat()
    {
        var vm = new ClientViewModel { IsCompanionPanelVisible = false };
        try
        {
            Receive(vm, new ChatPacket { Sender = "Professor", Message = "수업 공지", IsAnnouncement = true });
            Assert.Equal("수업 공지", vm.Announcement.Text);
            Assert.Equal(1, vm.ChatReading.UnreadCount);
            Assert.Contains("채팅 1", vm.CompanionPanelToggleText);
            Assert.Contains("공지", Assert.Single(vm.ChatMessages).Sender);
            vm.DismissAnnouncementCommand.Execute(null);
            Assert.False(vm.Announcement.IsVisible);
            Assert.Single(vm.ChatMessages);
            vm.IsCompanionPanelVisible = true;
            Assert.Equal(1, vm.ChatReading.UnreadCount);
            vm.ChatReading.ViewPosition(true);
            Receive(vm, new ChatPacket { Sender = "Alice", Message = "일반 채팅" });
            Assert.False(vm.Announcement.IsVisible);
            Assert.Equal(0, vm.ChatReading.UnreadCount);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public void OldChatPayloadDefaultsToOrdinaryChatAndAnnouncementRoundTrips()
    {
        var serializer = new PacketSerializer();
        var old = serializer.Deserialize<ChatPacket>(System.Text.Encoding.UTF8.GetBytes("{\"Sender\":\"Professor\",\"Message\":\"hello\"}"));
        Assert.False(old!.IsAnnouncement);
        var packet = new ChatPacket { Sender = "Professor", Message = "공지", IsAnnouncement = true };
        var decoded = serializer.Deserialize<ChatPacket>(serializer.Serialize(packet));
        Assert.True(decoded!.IsAnnouncement);
        Assert.Equal("공지", decoded.Message);
    }

    [Fact]
    public void DownloadCancelAffectsOnlyCurrentAttemptAndRetryGetsFreshToken()
    {
        var file = new SessionFileDescriptor(Guid.NewGuid(), Guid.NewGuid(), 1, "a.txt", 1, new string('a', 64), 1024);
        var item = new SessionFileItem(file, _ => Task.CompletedTask);
        var other = new SessionFileItem(file, _ => Task.CompletedTask);
        Assert.False(item.CancelCommand.CanExecute(null));
        var token = item.BeginDownload();
        var otherToken = other.BeginDownload();
        Assert.False(item.DownloadCommand.CanExecute(null));
        Assert.Throws<InvalidOperationException>(() => item.BeginDownload());
        item.CancelCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);
        Assert.False(otherToken.IsCancellationRequested);
        Assert.False(item.CancelCommand.CanExecute(null));
        item.EndDownload();
        Assert.True(item.DownloadCommand.CanExecute(null));
        var retry = item.BeginDownload();
        Assert.False(retry.IsCancellationRequested);
        item.EndDownload();
        other.EndDownload();
    }

    private static void Receive(ClientViewModel vm, ChatPacket packet) =>
        typeof(ClientViewModel).GetMethod("HandleChat", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [packet]);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
