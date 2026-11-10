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
    private readonly EduStream.Desktop.ChatScrollFollower _chatScroll;
    private readonly Services.MonitorSelectionStore _monitorPreferences = new();
    private AnnotationDesktopWindow? _annotation;
    private readonly HashSet<StudentScreenView> _studentViews = new();
    internal void RegisterStudentView(StudentScreenView view) => _studentViews.Add(view);

    public MainWindow()
    {
        EduStream.Desktop.AppTheme.Initialize();
        InitializeComponent();
        _viewModel = new ServerViewModel();
        _chatScroll = new(ChatListBox, _viewModel.ChatReading);
        _ = new EduStream.Desktop.ChatAttention(this, ChatListBox, _viewModel.Conversations);
        SourceInitialized += (_, _) =>
        {
            if (!CaptureExclusion.TryApply(this, out var error)) _viewModel.ReportCaptureExclusionFailure(error);
        };
        _viewModel.SessionManager.ConnectedStudentSharing = student => Dispatcher.Invoke(() =>
            _studentViews.Select(view => view.ConnectedSharingFor(student)).FirstOrDefault(id => id.HasValue));
        _viewModel.SessionManager.CloseStudentViewerAsync = async (student, token) =>
            await Dispatcher.InvokeAsync(async () =>
            {
                var targets = _studentViews.Where(view => view.HasViewerHistory(student)).ToArray();
                if (targets.Length == 0) return false;
                foreach (var view in targets)
                    if (!await view.DisconnectForRevokeAsync(student, token)) return false;
                return true;
            }).Task.Unwrap();
        // 방 비밀번호는 화면 공유로 노출되지 않게 PasswordBox로 받고, 세션을 열 때 한 번 읽은 뒤 비운다.
        _viewModel.RoomPasswordProvider = () =>
        {
            var password = RoomPasswordBox.Password;
            RoomPasswordBox.Clear();
            return password;
        };
        DataContext = _viewModel;
        _viewModel.ConfirmSessionEnd = count => MessageBox.Show(this,
            $"학생 {count}명이 접속해 있습니다. 종료하면 화면 공유·채팅·진행 중인 파일 전송이 끊깁니다.\n세션을 종료할까요?",
            "세션 종료 확인", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        try
        {
            if (_monitorPreferences.Load() is { } saved)
            {
                var monitor = Services.MonitorSelectionStore.Restore(saved, _viewModel.Monitors);
                if (monitor is not null) _viewModel.SelectedMonitor = monitor;
                else _viewModel.MonitorPreferenceNotice = "모니터 구성이 바뀌었습니다. 공유 전에 모니터를 다시 확인해 주세요.";
            }
        }
        catch (Exception)
        { _viewModel.MonitorPreferenceNotice = "이전 모니터 선택을 읽지 못했습니다. 공유 전에 모니터를 확인해 주세요."; }
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerViewModel.IsAnnouncementComposerOpen) && _viewModel.IsAnnouncementComposerOpen && IsLoaded)
                Dispatcher.BeginInvoke(new Action(() => AnnouncementInputBox.Focus()));
            if (e.PropertyName == nameof(ServerViewModel.SelectedMonitor) && _viewModel.SelectedMonitor is { } selected)
            {
                try
                {
                    _monitorPreferences.Save(selected, _viewModel.Monitors);
                    _viewModel.MonitorPreferenceNotice = "이 모니터 선택을 다음 실행에도 사용합니다.";
                }
                catch (Exception)
                { _viewModel.MonitorPreferenceNotice = "이번 선택은 적용했지만 다음 실행용 저장은 하지 못했습니다."; }
            }
            if (e.PropertyName == nameof(ServerViewModel.IsRdpSharing) && !_viewModel.IsRdpSharing)
            { _annotation?.Dispose(); _annotation = null; DrawingToggle.IsChecked = false; }
            // 세션이 열리면 세션 관리를 접고, 닫히거나 오류가 나면 펼쳐 상태를 바로 볼 수 있게 한다.
            if (e.PropertyName == nameof(ServerViewModel.IsSessionOpen))
                SessionExpander.IsExpanded = !_viewModel.IsSessionOpen;
            else if (e.PropertyName == nameof(ServerViewModel.IsStatusError) && _viewModel.IsStatusError)
                SessionExpander.IsExpanded = true;
        };
       
        Closing += OnClosing;
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _chatScroll.ShowLatest();
        _viewModel.Conversations.MarkSelectedRead();
    }
    private void ToggleTheme_Click(object sender, RoutedEventArgs e) => ((System.Windows.Controls.Button)sender).ToolTip = EduStream.Desktop.AppTheme.ToggleAndSave();

    private bool _closed;
    private bool _closing;

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        if (!_viewModel.CanEndSession()) return;
        _closing = true;
        IsEnabled = false;
        try
        {
            _annotation?.Dispose(); _annotation = null;
            await _viewModel.SessionManager.StopControlAsync();
            foreach (var view in _studentViews) await view.StopAsync();
            await _viewModel.ShutdownAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            MessageBox.Show("화면 공유나 원격 제어가 끝났는지 확인하지 못했습니다. 잠시 뒤 다시 닫아 주세요.", "EduStream");
            _closing = false; IsEnabled = true; return;
        }
        _closed = true;
        // 정리가 모두 동기로 끝나면 아직 Closing 처리 안이므로, 바로 Close하면 재진입 예외가 난다. 처리 뒤에 닫는다.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    private void CopyHostAddress_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedHostAddress is { } option)
        {
            Clipboard.SetText(option.Address);
        }
    }

    private void RefreshHostAddresses_Click(object sender, RoutedEventArgs e) => _viewModel.RefreshHostAddresses();

    /// <summary>Enter는 전송, Shift+Enter는 줄바꿈입니다. IME 조합을 확정하는 Enter(ImeProcessed)는 건드리지 않습니다.</summary>
    private void ChatInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Return || sender is not System.Windows.Controls.TextBox box) return;
        if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
        {
            var caret = box.CaretIndex;
            box.Text = box.Text.Insert(caret, Environment.NewLine);
            box.CaretIndex = caret + Environment.NewLine.Length;
        }
        else if (_viewModel.SendChatCommand.CanExecute(null))
        {
            _viewModel.SendChatCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void FileDropZone_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void FileDropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            _viewModel.PrepareDroppedFile(paths);
        e.Handled = true;
    }

private void ExpandAllStudents_Click(object sender, RoutedEventArgs e) => SetAllStudentsExpanded(true);
private void CollapseAllStudents_Click(object sender, RoutedEventArgs e) => SetAllStudentsExpanded(false);

    private int _lastStudentMatch = -1;
    private string _lastStudentQuery = "";
    private void FindStudent_Click(object sender, RoutedEventArgs e)
    {
        var query = StudentSearchBox.Text.Trim();
        if (query != _lastStudentQuery) _lastStudentMatch = -1;
        _lastStudentQuery = query;
        var index = Services.StudentNameSearch.FindNext(_viewModel.StudentRows.Select(x => x.ParticipantName).ToList(), query, _lastStudentMatch);
        if (index < 0)
        {
            StudentSearchResult.Text = query.Length == 0 ? "찾을 학생 이름을 입력해 주세요." : "일치하는 학생이 없습니다.";
            _lastStudentMatch = -1;
            return;
        }
        _lastStudentMatch = index;
        var row = _viewModel.StudentRows[index];
        StudentSearchResult.Text = row.ParticipantName + (row.IsConnected ? "" : " · 미접속이라 기록만 펼침");
        // 찾은 학생의 화면(접속 중일 때)과 입퇴장 기록을 펼친다. 다음 학생으로 넘어가면 이전 찾기로 펼친 것만 되돌린다.
        if (_searchOpened is { } previous && !ReferenceEquals(previous.Row, row))
        {
            if (!previous.WasScreenExpanded) previous.Row.IsScreenExpanded = false;
            if (!previous.WasHistoryExpanded) previous.Row.IsHistoryExpanded = false;
        }
        if (_searchOpened?.Row != row) _searchOpened = (row, row.IsScreenExpanded, row.IsHistoryExpanded);
        row.IsScreenExpanded = true;
        row.IsHistoryExpanded = true;
        // 펼친 뒤 높이가 바뀌므로 배치가 끝난 다음 카드 머리를 목록 맨 위로 올린다. 목록 순서·연결·제어 상태는 바꾸지 않는다.
        Dispatcher.BeginInvoke(new Action(() => ScrollStudentToTop(row)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private (StudentListRow Row, bool WasScreenExpanded, bool WasHistoryExpanded)? _searchOpened;

    private void ScrollStudentToTop(StudentListRow row)
    {
        var index = _viewModel.StudentRows.IndexOf(row);
        if (index < 0) return;
        StudentListItems.UpdateLayout();
        if (StudentListItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container) return;
        var top = container.TransformToAncestor(StudentListScroll).Transform(new System.Windows.Point(0, 0)).Y;
        StudentListScroll.ScrollToVerticalOffset(Math.Max(0, StudentListScroll.VerticalOffset + top));
    }

    private void StudentSearch_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Return) return;
        FindStudent_Click(sender, e);
        e.Handled = true;
    }

private void SetAllStudentsExpanded(bool expanded)
{
    foreach (var student in _viewModel.StudentRows) student.IsScreenExpanded = expanded;
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
        if (!_viewModel.IsRdpSharing || _viewModel.SelectedMonitor is not { } monitor) return;
        if (_annotation is null)
        {
            _annotation = new AnnotationDesktopWindow(monitor, _viewModel.AnnotationTools);
            _annotation.DrawingChanged += drawing => DrawingToggle.IsChecked = drawing;
        }
        else _annotation.SetDrawing(DrawingToggle.IsChecked == true);
    }
}
