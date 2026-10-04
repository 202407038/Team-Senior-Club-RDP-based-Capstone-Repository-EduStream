using System.Collections.ObjectModel;
using System.Collections.Specialized;
using EduStream.Server.ViewModels;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// U06: 다른 학생이 참가·퇴장해도 남아 있는 학생 카드의 펼침 상태가 유지되려면 목록을 Reset하지 않고
/// 바뀐 항목만 더하고 빼야 합니다. 화면 없이 목록 갱신 규칙을 검증합니다(실제 두 PC 조작 확인은 별도).
/// </summary>
public sealed class ParticipantListSyncTests
{
    private static List<NotifyCollectionChangedEventArgs> Record(ObservableCollection<string> list)
    {
        var events = new List<NotifyCollectionChangedEventArgs>();
        list.CollectionChanged += (_, e) => events.Add(e);
        return events;
    }

    [Fact]
    public void NewStudentJoining_OnlyAddsThatStudent()
    {
        var list = new ObservableCollection<string> { "Alice" };
        var events = Record(list);

        ServerViewModel.SyncParticipantList(list, new[] { "Alice", "Bob" });

        Assert.Equal(new[] { "Alice", "Bob" }, list);
        var change = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Add, change.Action);
        Assert.Equal("Bob", Assert.Single(change.NewItems!.Cast<string>()));
    }

    [Fact]
    public void StudentLeaving_OnlyRemovesThatStudent_AndKeepsOthersUntouched()
    {
        var list = new ObservableCollection<string> { "Alice", "Bob" };
        var bobBefore = list[1];
        var events = Record(list);

        ServerViewModel.SyncParticipantList(list, new[] { "Bob" });

        Assert.Equal(new[] { "Bob" }, list);
        Assert.Same(bobBefore, list[0]);
        var change = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Remove, change.Action);
        Assert.Equal("Alice", Assert.Single(change.OldItems!.Cast<string>()));
    }

    [Fact]
    public void UnchangedParticipants_RaiseNoChangeAtAll()
    {
        var list = new ObservableCollection<string> { "Alice", "Bob" };
        var events = Record(list);

        ServerViewModel.SyncParticipantList(list, new[] { "Alice", "Bob" });

        Assert.Empty(events);
    }

    [Fact]
    public void LeaveAndRejoin_NeverResetsTheList()
    {
        var list = new ObservableCollection<string> { "Alice", "Bob" };
        var events = Record(list);

        ServerViewModel.SyncParticipantList(list, new[] { "Bob" });
        ServerViewModel.SyncParticipantList(list, new[] { "Bob", "Alice" });

        Assert.DoesNotContain(events, e => e.Action == NotifyCollectionChangedAction.Reset);
        Assert.Equal(new[] { "Bob", "Alice" }, list);
    }
}
