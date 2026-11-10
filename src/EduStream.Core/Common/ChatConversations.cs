using System.Collections.ObjectModel;
using System.Collections.Specialized;
using EduStream.Core.Models;

namespace EduStream.Core.Common;

/// <summary>대화별 목록·새 메시지 수. 개인 대화를 전체 대화로 자동 전환하지 않는다.</summary>
public sealed class ChatConversations : ObservableObject
{
    private ChatConversation _selected;
    private Guid? _sessionId;
    private string? _scope;
    public bool KeepHistoryAcrossSessions { get; set; }
    /// <summary>학생 앱: 새 세션 참가 때 대화는 두고 이전 세션의 시스템 안내만 지운다.</summary>
    public bool ResetNoticesOnNewSession { get; set; }
    public ObservableCollection<ChatConversation> Items { get; } = [];
    public event Action? AttentionRequested;
    public event Action<int>? VisibleMessagesAdded;
    public bool IsReadingActive { get; set; }
    public int UnreadCount => Items.Sum(x => x.UnreadCount);
    public string Summary => UnreadCount == 0 ? "대화 선택" : $"새 메시지 {UnreadCount}개 · 대화 선택";
    public ChatConversation Selected
    {
        get => _selected;
        set
        {
            if (value is null || !Items.Contains(value)) return;
            if (SetProperty(ref _selected, value)) MarkSelectedRead();
        }
    }

    public ChatConversations(ObservableCollection<ChatLine> publicMessages)
    {
        _selected = Add("", "전체", publicMessages);
    }

    public ChatConversation GetOrAdd(string key, string name) => Items.FirstOrDefault(x => x.Key == key) ?? Add(key, name, []);
    public void SetSession(Guid sessionId)
    {
        if (_sessionId == sessionId) return;
        _sessionId = sessionId;
        if (!KeepHistoryAcrossSessions) { ClearPrivateHistory(); return; }
        if (!ResetNoticesOnNewSession) return;
        // 대화는 남기되 이전 세션의 연결 끊김·원격 제어 같은 안내는 지우고, 새 세션 시작 위치를 한 줄로 표시한다.
        foreach (var item in Items)
            for (var i = item.Messages.Count - 1; i >= 0; i--)
                if (item.Messages[i].IsSystem) item.Messages.RemoveAt(i);
        var publicMessages = Items[0].Messages;
        if (publicMessages.Count > 0)
            publicMessages.Add(ChatLine.System($"── 새 수업 참가 · {DateTime.Now:HH:mm} ──"));
    }
    // 학생 앱에서 다른 교수자/다른 학생으로 참가할 때 이전 전체·개인 기록을 섞지 않는다.
    public void SetScope(string scope)
    {
        if (_scope == scope) return;
        _scope = scope;
        Items[0].Messages.Clear();
        ClearPrivateHistory();
    }
    private void ClearPrivateHistory()
    {
        foreach (var item in Items.Skip(1).ToArray()) Items.Remove(item);
        Selected = Items[0];
        MarkSelectedRead();
        NotifyCounts();
    }
    public void MarkSelectedRead()
    {
        if (!IsReadingActive) return;
        Selected.UnreadCount = 0;
        NotifyCounts();
    }
    public void RequestAttention() => AttentionRequested?.Invoke();
    private ChatConversation Add(string key, string name, ObservableCollection<ChatLine> messages)
    {
        var item = new ChatConversation(key, name, messages);
        Items.Add(item);
        messages.CollectionChanged += (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Add) return;
            if (ReferenceEquals(Selected, item)) VisibleMessagesAdded?.Invoke(args.NewItems?.Count ?? 0);
            var count = args.NewItems?.Cast<ChatLine>().Count(x => !x.IsSelf && !x.IsSystem) ?? 0;
            if (count == 0) return;
            if (!ReferenceEquals(Selected, item) || !IsReadingActive) item.UnreadCount += count;
            NotifyCounts();
            AttentionRequested?.Invoke();
        };
        return item;
    }
    private void NotifyCounts() { OnPropertyChanged(nameof(UnreadCount)); OnPropertyChanged(nameof(Summary)); }
}

public sealed class ChatConversation(string key, string name, ObservableCollection<ChatLine> messages) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public Guid? ConnectionId { get; set; }
    public ObservableCollection<ChatLine> Messages { get; } = messages;
    private bool _isAvailable = true;
    public bool IsAvailable
    {
        get => _isAvailable;
        set { if (SetProperty(ref _isAvailable, value)) OnPropertyChanged(nameof(Label)); }
    }
    private int _unread;
    public int UnreadCount
    {
        get => _unread;
        internal set { if (SetProperty(ref _unread, value)) OnPropertyChanged(nameof(Label)); }
    }
    public string Label => Name + (Key.Length == 0 ? "" : " · 1:1") +
        (IsAvailable ? "" : " · 연결 종료/미지원") + (UnreadCount == 0 ? "" : $" · 새 메시지 {UnreadCount}");
}
