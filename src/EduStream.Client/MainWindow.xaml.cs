using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EduStream.Client.ViewModels;

namespace EduStream.Client;

public partial class MainWindow : Window
{
    private readonly EduStream.Desktop.ChatScrollFollower _chatScroll;
    private bool _shutdownComplete;
    private bool _shutdownInProgress;
    public MainWindow()
    {
        EduStream.Desktop.AppTheme.Initialize();
        InitializeComponent();
        DataContext = new ClientViewModel(joinPreferences: new Services.JoinPreferencesStore());
        _chatScroll = new(ChatListBox, ((ClientViewModel)DataContext).ChatReading);
        _ = new EduStream.Desktop.ChatAttention(this, ChatListBox, ((ClientViewModel)DataContext).Conversations);
        if (DataContext is ClientViewModel vm)
        {
            vm.AttachRdpHost(RdpHost);
            // 방 비밀번호는 참가를 시도할 때 한 번 읽고 바로 비운다.
            vm.RoomPasswordProvider = () =>
            {
                var password = RoomPasswordBox.Password;
                RoomPasswordBox.Clear();
                return password;
            };
            // 참가 화면에서는 창을 폼 크기로 줄이고, 강의 화면에서는 원래 크기로 되돌린다.
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ClientViewModel.IsLectureViewActive))
                    ApplyLayoutMode(vm.IsLectureViewActive);
            };
            // 모니터 정보를 읽을 창 핸들이 생긴 뒤 배치한다. 초기에는 참가 폼 크기만 준비한다.
            Width = JoinWidth;
            MinWidth = 420;
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
            Loaded += (_, _) => ApplyLayoutMode(vm.IsLectureViewActive);
        }
        Closing += async (_, e) =>
        {
            if (_shutdownComplete) return;
            e.Cancel = true;
            if (_shutdownInProgress) return;
            _shutdownInProgress = true;
            IsEnabled = false;
            try
            {
                if (DataContext is ClientViewModel model) await model.ShutdownAsync();
                _shutdownComplete = true;
                // 정리가 동기로 끝나면 아직 Closing 처리 안이라 바로 Close하면 재진입 예외가 난다. 처리 뒤에 닫는다.
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                System.Windows.MessageBox.Show(this,
                    "강의 화면 연결을 완전히 끝내지 못했습니다. 닫기를 다시 눌러 주세요.",
                    "EduStream 종료 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _shutdownInProgress = false; if (!_shutdownComplete) IsEnabled = true; }
        };
    }

    private const double JoinWidth = 520;
    /// <summary>참가 화면 입력칸에서 Enter를 누르면 '세션 참여'와 같다. 체크박스·버튼에 포커스가 있을 때는 그대로 둔다.</summary>
    private void JoinForm_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Return || e.OriginalSource is not (System.Windows.Controls.TextBox or System.Windows.Controls.PasswordBox)) return;
        if (DataContext is ClientViewModel model && model.JoinSessionCommand.CanExecute(null)) model.JoinSessionCommand.Execute(null);
        e.Handled = true;
    }

    private void SubmissionDropZone_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) && DataContext is ClientViewModel { IsSubmitting: false }
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void SubmissionDropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        // 끌어 놓으면 제출 대기 파일로만 정한다. 전송은 '제출하기'를 눌러야 한다.
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            (DataContext as ClientViewModel)?.PrepareSubmissionFile(paths);
        e.Handled = true;
    }

    private void FileList_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => Behaviors.NestedScrollHandoff.Handle(sender, e);
    private void ToggleTheme_Click(object sender, RoutedEventArgs e) => ((System.Windows.Controls.Button)sender).ToolTip = EduStream.Desktop.AppTheme.ToggleAndSave();
    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _chatScroll.ShowLatest();
        ((ClientViewModel)DataContext).Conversations.MarkSelectedRead();
    }
    private double _lectureWidth = 1360;
    private double _lectureHeight = 820;
    private bool _lectureMaximized;
    private bool? _lectureMode;
    private int _layoutGeneration;

    private void ApplyLayoutMode(bool lecture)
    {
        if (!IsLoaded) return;
        if (_lectureMode == lecture) return;
        var previous = _lectureMode;
        _lectureMode = lecture;
        var generation = ++_layoutGeneration;
        var placement = WindowPlacement.Capture(this);
        var availableWidth = placement.WorkArea.Width / placement.ScaleX;
        var availableHeight = placement.WorkArea.Height / placement.ScaleY;

        if (lecture)
        {
            SizeToContent = SizeToContent.Manual;
            MaxHeight = double.PositiveInfinity;
            MinWidth = Math.Min(960, availableWidth);
            MinHeight = Math.Min(640, availableHeight);
            Width = Math.Min(_lectureWidth, availableWidth);
            Height = Math.Min(_lectureHeight, availableHeight);
        }
        else
        {
            if (previous == true)
            {
                _lectureMaximized = WindowState == WindowState.Maximized;
                var bounds = _lectureMaximized ? RestoreBounds : new Rect(0, 0, ActualWidth, ActualHeight);
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    _lectureWidth = bounds.Width;
                    _lectureHeight = bounds.Height;
                }
            }
            WindowState = WindowState.Normal;
            MinWidth = Math.Min(420, availableWidth);
            MinHeight = 0;
            MaxHeight = availableHeight;
            Width = Math.Min(JoinWidth, availableWidth);
            SizeToContent = SizeToContent.Height;
        }

        // 주 모니터로 재중앙 정렬하지 않는다. 이전 위치를 유지하고 화면 밖으로 나간 만큼만 보정한다.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (generation != _layoutGeneration || _shutdownComplete) return;
            if (WindowState != WindowState.Normal) return;
            UpdateLayout();
            WindowPlacement.RestorePosition(this, placement);
            if (lecture && _lectureMaximized) WindowState = WindowState.Maximized;
        }), DispatcherPriority.ApplicationIdle);
    }

    private void UseRoomPasswordCheck_Unchecked(object sender, RoutedEventArgs e) => RoomPasswordBox.Clear();

    /// <summary>Enter는 전송, Shift+Enter는 줄바꿈입니다. IME 조합을 확정하는 Enter(ImeProcessed)는 건드리지 않습니다.</summary>
    private void ChatInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Return || sender is not System.Windows.Controls.TextBox box) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var caret = box.CaretIndex;
            box.Text = box.Text.Insert(caret, Environment.NewLine);
            box.CaretIndex = caret + Environment.NewLine.Length;
        }
        else if (DataContext is ClientViewModel model && model.SendChatCommand.CanExecute(null))
        {
            model.SendChatCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private bool _systemBorder;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 앱이 그린 사각 테두리는 Windows 11의 둥근 모서리에 잘려 계단처럼 보인다.
        // 교수자 창처럼 시스템이 둥근 모서리와 테두리를 함께 그리게 한다. Windows 10에서는 실패하므로 기존 테두리를 유지한다.
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int round = 2; // DWMWCP_ROUND
        _systemBorder = DwmSetWindowAttribute(handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int)) == 0;
        if (_systemBorder) BorderThickness = new Thickness(0);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        var maximized = WindowState == WindowState.Maximized;
        // 사용자 정의 상단바는 최대화 때 화면 밖으로 넘치므로 테두리 두께만큼 안쪽으로 당긴다.
        WindowRoot.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        // Windows 11은 창 테두리를 직접 그린다. 그 외에는 일반 크기에서만 1px 테두리를 그린다.
        BorderThickness = maximized || _systemBorder ? new Thickness(0) : new Thickness(1);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "이전 크기로" : "최대화";
    }
}
