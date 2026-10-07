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
    private AnnotationDesktopWindow? _annotation;
    private readonly HashSet<StudentScreenView> _studentViews = new();
    internal void RegisterStudentView(StudentScreenView view) => _studentViews.Add(view);

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new ServerViewModel();
        SourceInitialized += (_, _) => CaptureExclusion.Apply(this);
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
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerViewModel.IsRdpSharing) && !_viewModel.IsRdpSharing)
            { _annotation?.Dispose(); _annotation = null; DrawingToggle.IsChecked = false; }
        };
       
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
        try
        {
            _annotation?.Dispose(); _annotation = null;
            await _viewModel.SessionManager.StopControlAsync();
            foreach (var view in _studentViews) await view.StopAsync();
            await _viewModel.ShutdownAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show("공유 종료를 확인해 주세요: " + ex.GetType().Name, "EduStream");
            _closing = false; IsEnabled = true; return;
        }
        _closed = true; Close();
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

    private async void FileDropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            await _viewModel.RegisterDroppedFilesAsync(paths);
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
        if (!_viewModel.IsRdpSharing || _viewModel.SelectedMonitor is not { } monitor) return;
        if (_annotation is null)
        {
            _annotation = new AnnotationDesktopWindow(monitor, _viewModel.AnnotationTools);
            _annotation.DrawingChanged += drawing => DrawingToggle.IsChecked = drawing;
        }
        else _annotation.SetDrawing(DrawingToggle.IsChecked == true);
    }
}
