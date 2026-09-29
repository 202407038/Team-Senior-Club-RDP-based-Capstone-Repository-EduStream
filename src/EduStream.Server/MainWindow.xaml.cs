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

        // ChatMessages�� �� �޽����� �߰��� �� WPF�� ���̾ƿ��� �� �׸� �� �� �Ʒ��� ��ũ��
        ((INotifyCollectionChanged)_viewModel.ChatMessages).CollectionChanged += (s, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && ChatListBox.Items.Count > 0)
            {
                // WPF UI �������� �Ϸ�� ����(DispatcherPriority.Background) ��ũ�� ����
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    // 1. ListBox ������ ������ ��ũ��
                    var lastItem = ChatListBox.Items[ChatListBox.Items.Count - 1];
                    ChatListBox.ScrollIntoView(lastItem);

                    // 2. ���� ScrollViewer�� ã�� ������ �� �Ʒ� �ٴ����� ��ũ��
                    var scrollViewer = GetScrollViewer(ChatListBox);
                    scrollViewer?.ScrollToBottom();
                }));
            }
        };
    }

    // ListBox ������ ScrollViewer ��Ҹ� ã�� ����� �޼���
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

    private void CopyInvitationPassword_Click(object sender, RoutedEventArgs e)
    {
        var password = InvitationParticipant.SelectedItem is string participant
            ? _viewModel.GetInvitationPassword(participant) : null;
        if (password is null)
        {
            MessageBox.Show("학생을 선택해 주세요. 초대가 만료됐다면 학생 앱에서 RDP 재접속을 눌러 주세요.", "EduStream");
            return;
        }
        try { Clipboard.SetText(password); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show("클립보드를 사용할 수 없습니다. 잠시 후 다시 시도해 주세요.", "EduStream");
        }
    }
}