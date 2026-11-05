using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EduStream.Core.Common;
using ListBox = System.Windows.Controls.ListBox;

namespace EduStream.Desktop;

/// <summary>양 앱의 채팅 스크롤 정책. 창을 다시 표시해도 읽던 위치를 강제로 바꾸지 않는다.</summary>
internal sealed class ChatScrollFollower
{
    private readonly ListBox _list;
    private readonly ChatReadingState _state;
    private ScrollViewer? _scroll;

    public ChatScrollFollower(ListBox list, ChatReadingState state)
    {
        _list = list;
        _state = state;
        list.Loaded += (_, _) =>
        {
            if (_scroll is not null) return;
            _scroll = FindScroll(list);
            if (_scroll is not null) _scroll.ScrollChanged += OnScroll;
        };
        list.IsVisibleChanged += (_, _) => _state.IsVisible = list.IsVisible;
        _state.IsVisible = list.IsVisible;
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Add || !_state.ShouldFollow) return;
            list.Dispatcher.BeginInvoke(new Action(() =>
            {
                // 큐를 기다리는 동안 사용자가 과거 메시지로 이동했을 수 있다.
                if (_state.ShouldFollow) ScrollToLatest();
            }), DispatcherPriority.Background);
        };
    }

    private void OnScroll(object sender, ScrollChangedEventArgs args)
    {
        if (_scroll is null || !ReferenceEquals(args.OriginalSource, _scroll) || !_list.IsVisible ||
            args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0) return;
        if (args.VerticalChange != 0)
            _state.ViewPosition(_scroll.ScrollableHeight - _scroll.VerticalOffset <= 0.5);
    }

    public void ShowLatest()
    {
        if (!_list.IsVisible) return;
        _state.ViewPosition(true);
        ScrollToLatest();
    }

    private void ScrollToLatest()
    {
        if (_list.Items.Count > 0) _list.ScrollIntoView(_list.Items[^1]);
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScroll(child) is { } nested) return nested;
        }
        return null;
    }
}
