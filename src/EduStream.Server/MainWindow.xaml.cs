using EduStream.Server.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
namespace EduStream.Server;

public partial class MainWindow : Window
{
    private readonly ServerViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new ServerViewModel();
        // 방 비밀번호는 화면 공유로 노출되지 않게 PasswordBox로 받고, 세션을 열 때 한 번 읽은 뒤 비운다.
        _viewModel.RoomPasswordProvider = () =>
        {
            var password = RoomPasswordBox.Password;
            RoomPasswordBox.Clear();
            return password;
        };
        DataContext = _viewModel;
       
        Closing += OnClosing;
        Loaded += (_, _) =>
        {
            ((System.Collections.Specialized.INotifyCollectionChanged)_viewModel.ChatMessages).CollectionChanged += (s, e) =>
            {
                if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ChatListBox.Items.Count > 0)
                        ChatListBox.ScrollIntoView(ChatListBox.Items[^1]);
                }), DispatcherPriority.Background);
            };
        };
    }

    private bool _closed;
    private bool _closing;

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        try { await _viewModel.ShutdownAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show("공유 종료를 확인해 주세요: " + ex.GetType().Name, "EduStream");
        }
        finally { _closed = true; Close(); }
    }
    private void CopyConnectionCode_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ConnectionCodeBox.Text))
        {
            System.Windows.Clipboard.SetText(ConnectionCodeBox.Text);
        }
    }


private void ExpandAllStudents_Click(object sender, RoutedEventArgs e) => SetAllStudentsExpanded(true);
private void CollapseAllStudents_Click(object sender, RoutedEventArgs e) => SetAllStudentsExpanded(false);

private void SetAllStudentsExpanded(bool expanded)
{
    for (int i = 0; i < StudentListItems.Items.Count; i++)
    {
        if (StudentListItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            continue;

        var expander = FindVisualChild<Expander>(container, "StudentExpander");
        if (expander is not null)
        {
            expander.IsExpanded = expanded;
        }
    }
}

    private static ScrollViewer? GetScrollViewer(DependencyObject depObj)
    {
        if (depObj is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var result = GetScrollViewer(VisualTreeHelper.GetChild(depObj, i));
            if (result != null) return result;
        }
        return null;
    }
    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
{
    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
    {
        var child = VisualTreeHelper.GetChild(parent, i);
        if (child is T typed && typed.Name == name) return typed;
        var found = FindVisualChild<T>(child, name);
        if (found is not null) return found;
    }
    return null;
}
private void DrawingToggle_Click(object sender, RoutedEventArgs e)
    {
        // TODO: 실제 판서 기능은 엔진 담당자 연결 후 구현. 지금은 ON/OFF 겉모습만 바뀝니다.
    }
}
