namespace EduStream.Core.Common;

/// <summary>읽던 위치를 유지하면서, 실제로 맨 아래를 볼 때만 읽음 처리한다.</summary>
public sealed class ChatReadingState : ObservableObject
{
    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        private set
        {
            if (!SetProperty(ref _unreadCount, value)) return;
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(NewMessagesText));
        }
    }
    public bool HasUnread => UnreadCount > 0;
    public string NewMessagesText => $"새 메시지 {UnreadCount}개 ↓";
    public bool IsAtBottom { get; private set; } = true;
    public bool IsVisible { get; set; } = true;
    public bool ShouldFollow => IsVisible && IsAtBottom && !HasUnread;

    public void Receive(int count = 1)
    {
        if (count > 0 && !ShouldFollow) UnreadCount += count;
    }

    // 콘텐츠 높이 변화가 아니라 사용자 스크롤/명시적 이동 결과에만 호출한다.
    public void ViewPosition(bool atBottom)
    {
        IsAtBottom = atBottom;
        if (IsVisible && atBottom) UnreadCount = 0;
    }
}
