using System.Collections.ObjectModel;

namespace EduStream.Core.Common;

/// <summary>일반 알림은 3초, 중요 알림은 확인하면 닫기. 공지함 기록은 유지한다.</summary>
public sealed class AnnouncementBanner : ObservableObject
{
    private readonly TimeProvider _time;
    private DateTimeOffset _expires;
    private string _text = "";
    private Guid? _sessionId;
    private bool _isOpen;
    public AnnouncementBanner(TimeProvider? time = null) => _time = time ?? TimeProvider.System;
    public ObservableCollection<AnnouncementItem> Items { get; } = new();
    public ObservableCollection<AnnouncementItem> ImportantItems { get; } = new();
    public event Action<AnnouncementItem>? Confirmed;
    public bool IsOpen { get => _isOpen; set => SetProperty(ref _isOpen, value); }
    public int UnreadCount => Items.Count(x => !x.IsRead);
    public string InboxLabel => $"공지함 · 미확인 {UnreadCount}";
    public bool HasImportant => ImportantItems.Count > 0;
    public bool IsEmpty => Items.Count == 0;

    public void SetSession(Guid sessionId)
    {
        if (_sessionId == sessionId) return;
        _sessionId = sessionId;
        Items.Clear(); ImportantItems.Clear(); Dismiss(); IsOpen = false;
        NotifyInbox();
    }

    public AnnouncementItem Receive(Guid id, string text, bool important, DateTimeOffset sentAt, bool history = false)
    {
        var existing = Items.FirstOrDefault(x => x.Id == id);
        if (existing is not null) return existing;
        var item = new AnnouncementItem(id, text, important, sentAt, changed =>
        {
            if (changed.IsDismissed) ImportantItems.Remove(changed);
            NotifyInbox();
        }, confirmed => Confirmed?.Invoke(confirmed));
        Items.Insert(0, item);
        if (important) ImportantItems.Add(item);
        else if (!history) Show(text);
        NotifyInbox();
        return item;
    }

    private void NotifyInbox()
    {
        OnPropertyChanged(nameof(UnreadCount)); OnPropertyChanged(nameof(InboxLabel));
        OnPropertyChanged(nameof(HasImportant)); OnPropertyChanged(nameof(IsEmpty));
    }
    public string Text
    {
        get => _text;
        private set { if (SetProperty(ref _text, value)) OnPropertyChanged(nameof(IsVisible)); }
    }
    public bool IsVisible => Text.Length > 0;
    public void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Text = text;
        _expires = _time.GetUtcNow().AddSeconds(3);
    }
    public void Refresh() { if (IsVisible && _time.GetUtcNow() >= _expires) Dismiss(); }
    public void Dismiss() => Text = "";
}

public sealed class AnnouncementItem : ObservableObject
{
    private bool _isRead;
    private bool _isDismissed;
    public Guid Id { get; }
    public string Text { get; }
    public bool IsImportant { get; }
    public DateTimeOffset SentAt { get; }
    public DateTimeOffset LocalSentAt => SentAt.ToLocalTime();
    public bool IsRead => _isRead;
    public bool IsDismissed => _isDismissed;
    public string ReadLabel => IsRead ? "확인함" : "미확인";
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand DismissCommand { get; }
    public AnnouncementItem(Guid id, string text, bool important, DateTimeOffset sentAt,
        Action<AnnouncementItem> changed, Action<AnnouncementItem> confirmed)
    {
        Id = id; Text = text; IsImportant = important; SentAt = sentAt;
        ConfirmCommand = new RelayCommand(() =>
        {
            if (!_isRead) { _isRead = true; OnPropertyChanged(nameof(IsRead)); OnPropertyChanged(nameof(ReadLabel)); }
            if (IsImportant && !_isDismissed)
            {
                _isDismissed = true;
                OnPropertyChanged(nameof(IsDismissed));
            }
            changed(this);
            // 확인 전송 실패 뒤에도 다시 누를 수 있어야 한다.
            confirmed(this);
        });
        DismissCommand = new RelayCommand(() =>
        {
            _isDismissed = true; OnPropertyChanged(nameof(IsDismissed)); changed(this);
        });
    }
}
