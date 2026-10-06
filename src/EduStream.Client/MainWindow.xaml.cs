using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EduStream.Client.ViewModels;

namespace EduStream.Client;

public partial class MainWindow : Window
{
    private bool _shutdownComplete;
    private bool _shutdownInProgress;
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new ClientViewModel();
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
        // 새 메시지가 추가되면 채팅 목록을 맨 아래로 내린다.
        Loaded += (_, _) =>
        {
            if (DataContext is not ClientViewModel chatModel) return;
            ((INotifyCollectionChanged)chatModel.ChatMessages).CollectionChanged += (_, args) =>
            {
                if (args.Action != NotifyCollectionChangedAction.Add) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ChatListBox.Items.Count > 0)
                        ChatListBox.ScrollIntoView(ChatListBox.Items[^1]);
                }), DispatcherPriority.Background);
            };
        };
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
                Close();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this,
                    "화면 공유 연결을 완전히 종료하지 못했습니다. 다시 닫기를 눌러 재시도해 주세요.\n" + ex.Message,
                    "EduStream 종료 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _shutdownInProgress = false; if (!_shutdownComplete) IsEnabled = true; }
        };
    }

    private const double JoinWidth = 520;
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

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        var maximized = WindowState == WindowState.Maximized;
        // 사용자 정의 상단바는 최대화 때 화면 밖으로 넘치므로 테두리 두께만큼 안쪽으로 당긴다.
        WindowRoot.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "이전 크기로" : "최대화";
    }
}
