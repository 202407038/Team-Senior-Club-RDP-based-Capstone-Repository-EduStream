using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EduStream.Server.ViewModels;

namespace EduStream.Server;

public partial class MainWindow : Window
{
    private readonly ServerViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = DataContext as ServerViewModel ?? new ServerViewModel();
        DataContext = _viewModel;

        // ChatMessages에 새 메시지가 추가될 때 WPF가 레이아웃을 다 그린 뒤 맨 아래로 스크롤
        ((INotifyCollectionChanged)_viewModel.ChatMessages).CollectionChanged += (s, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && ChatListBox.Items.Count > 0)
            {
                // WPF UI 렌더링이 완료된 직후(DispatcherPriority.Background) 스크롤 실행
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    // 1. ListBox 마지막 아이템 스크롤
                    var lastItem = ChatListBox.Items[ChatListBox.Items.Count - 1];
                    ChatListBox.ScrollIntoView(lastItem);

                    // 2. 내부 ScrollViewer를 찾아 강제로 맨 아래 바닥으로 스크롤
                    var scrollViewer = GetScrollViewer(ChatListBox);
                    scrollViewer?.ScrollToBottom();
                }));
            }
        };
    }

    // ListBox 내부의 ScrollViewer 요소를 찾는 도우미 메서드
    private static ScrollViewer? GetScrollViewer(DependencyObject depObj)
    {
        if (depObj is ScrollViewer sv) return sv;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = VisualTreeHelper.GetChild(depObj, i);
            var result = GetScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.AttachRdpSurface(RdpPreviewHost);
    }
}