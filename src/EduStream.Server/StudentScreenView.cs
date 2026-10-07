using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using AxRDPCOMAPILib;
using EduStream.Server.Services;
using EduStream.Server.ViewModels;
using EduStream.ShareViewer;
using Forms = System.Windows.Forms;
using Brushes = System.Windows.Media.Brushes;

namespace EduStream.Server;

/// <summary>학생별 독립 수신 표면. 크게 보기는 기존 뷰어를 이동하며 두 번째 연결을 만들지 않는다.</summary>
public sealed class StudentScreenView : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(ServerViewModel), typeof(StudentScreenView));
    public ServerViewModel Model { get => (ServerViewModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    // 카드: 상태 + 미리보기 + 넓은 "원격 제어" 버튼 하나. 버튼을 누르면 같은 뷰어가 별도 창으로 옮겨가고,
    // 창 상단에 원격 제어 · 다시 연결 · 화면 맞춤이 있다. 두 번째 연결은 만들지 않는다.
    private readonly TextBlock _cardStatus = new() { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly WindowsFormsHost _host = new() { Height = 180 };
    private readonly DockPanel _windowLayout = new();
    private readonly StackPanel _root = new();
    private readonly ProfessorReception _reception = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly System.Windows.Controls.Button _control = ThemedButton("원격 제어", enabled: false);
    private ReverseCollaborationRouter? _router;
    private ReverseInvitationDelivery? _delivery;
    private AxRDPViewer? _viewer;
    private ViewerZoomSurface? _surface;
    private ProfessorViewerConnection? _connection;
    private Window? _large;
    private bool _loaded;
    private bool _subscribed;
    private bool _stopped;
    private string? _viewerKey;
    private Guid? _viewerInvitationId;
    private readonly HashSet<Guid> _usedInvitations = new();
    private EduStream.Core.Collaboration.ParticipantConnection? _viewerStudent;
    private readonly HashSet<EduStream.Core.Collaboration.ParticipantConnection> _releasedConnections = new();
    private string StudentName => DataContext as string ?? string.Empty;
    public Guid? ConnectedSharingFor(EduStream.Core.Collaboration.ParticipantConnection student) =>
        _viewerStudent == student && _connection?.IsConnectionLive == true ? _delivery?.Invitation.SharingId : null;

    private static System.Windows.Controls.Button ThemedButton(string text, bool enabled = true) => new()
    {
        Content = text, IsEnabled = enabled, Margin = new Thickness(3), Padding = new Thickness(10, 5, 10, 5), FontSize = 12,
        Style = (Style)System.Windows.Application.Current.FindResource("GhostButton"),
    };

    public StudentScreenView()
    {
        var open = new System.Windows.Controls.Button
        {
            Content = "원격 제어", Height = 34, Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton"),
        };
        open.Click += async (_, _) => await OpenWindowAsync();
        _root.Children.Add(_cardStatus); _root.Children.Add(_host); _root.Children.Add(open);
        Content = _root;
        IsVisibleChanged += async (_, _) =>
        {
            if (!_stopped && IsVisible && _delivery is { } delivery && _connection?.IsConnectionLive != true)
                await ConnectAsync(delivery);
        };

        // 학생 화면 창 상단: 원격 제어 · 다시 연결 · 화면 맞춤. 한 번만 만들어 창을 다시 열 때도 재사용한다.
        _control.Click += async (_, _) => await ChangeControlAsync();
        var retry = ThemedButton("새로고침");
        retry.Click += async (_, _) => await RefreshViewAsync();
        var fit = ThemedButton("화면 맞춤");
        fit.Click += (_, _) => _surface?.Fit();
        var bar = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        bar.Children.Add(_control); bar.Children.Add(retry); bar.Children.Add(fit); bar.Children.Add(_status);
        var toolbar = new Border
        {
            Child = bar, Padding = new Thickness(8, 6, 8, 6), BorderThickness = new Thickness(0, 0, 0, 1),
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BrushBgSurface"),
            BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BrushBorder"),
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        _windowLayout.Children.Add(toolbar);
        SetStatus("학생 화면 공유 연결 대기");

        Loaded += (_, _) =>
        {
            if (_stopped) return;
            _loaded = true;
            (Window.GetWindow(this) as MainWindow)?.RegisterStudentView(this);
            if (!_subscribed && Model is not null)
            {
                Model.PropertyChanged += ModelChanged;
                Model.SessionManager.ControlStateChanged += ControlChanged;
                _subscribed = true;
            }
            AttachRouter();
        };
        // 접기는 보기 전용 뷰어 수명을 끊지 않는다. 초대 교체/참가 종료 때만 해제한다.
        Unloaded += (_, _) =>
        {
            _loaded = false;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (!IsLoaded && Model is not null && !Model.Participants.Contains(StudentName)) await StopAsync();
            }));
        };
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerViewModel.IsSessionOpen)) AttachRouter();
    }
    private void ControlChanged(EduStream.Core.Collaboration.RemoteControlState state)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_stopped || _viewerStudent != state.Student) return;
            var active = state.Phase == EduStream.Core.Collaboration.ControlPhase.Active;
            _control.Content = active ? "제어 중지" : "원격 제어";
            if (_surface is not null) _surface.WheelZoomEnabled = !active;
            SetStatus(active ? "원격 제어 중" : "보기 전용 · 제어 " + state.Phase);
        }));
    }

    private void AttachRouter()
    {
        if (_stopped) return;
        var router = Model?.SessionManager.ReverseCollaboration;
        if (!ReferenceEquals(router, _router))
        {
            if (_router is not null) { _router.InvitationReady -= Ready; _router.InvitationWithdrawn -= Withdrawn; }
            _router = router;
            if (_router is not null) { _router.InvitationReady += Ready; _router.InvitationWithdrawn += Withdrawn; }
        }
        if (_router is null) { _ = ClearAsync(); return; }
        var student = Model!.SessionManager.Participants;
        // 현재 목록 이름에 해당하는 인증된 초대만 사용한다.
        foreach (var snapshot in student.Participants)
            if (snapshot.DisplayName == StudentName && _router.TryGetInvitation(snapshot.Connection.ConnectionId) is { } delivery)
            { Ready(delivery); return; }
        SetStatus("학생 화면 공유 연결 대기");
    }

    private void Ready(ReverseInvitationDelivery delivery)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_stopped) return;
            if (delivery.DisplayName != StudentName) return;
            if (ReferenceEquals(_delivery, delivery) && _connection?.IsConnectionLive == true) return;
            _delivery = delivery;
            if (_loaded && (IsVisible || _large is not null)) await ConnectAsync(delivery);
        }));
    }

    private void SetStatus(string text) { _status.Text = text; _cardStatus.Text = text; }

    private void Withdrawn(EduStream.Core.Collaboration.ParticipantConnection student, Guid invitationId)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_delivery?.Student == student && _delivery.Invitation.InvitationId == invitationId) await ClearAsync();
        }));
    }

    private async Task ConnectAsync(ReverseInvitationDelivery delivery, bool retry = false)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_stopped || _router?.TryGetInvitation(delivery.Student.ConnectionId) != delivery) return;
            // Loaded/IsVisibleChanged/초대 알림이 겹쳐도 연결 중인 동일 뷰어를 끊지 않는다.
            if (!retry && _viewerStudent == delivery.Student && _viewerInvitationId == delivery.Invitation.InvitationId &&
                _connection is { Failed: false, Terminated: false } && _viewer is { IsDisposed: false }) return;
            await ReleaseAsync();
            _delivery = delivery;
            var viewer = new AxRDPViewer();
            _viewer = viewer;
            viewer.OnConnectionEstablished += (_, _) => { SetStatus("학생 화면 연결됨 · 보기 전용"); _control.IsEnabled = true; };
            viewer.OnConnectionFailed += (_, _) => { SetStatus("학생 화면 연결 실패 · 새로고침을 눌러 주세요."); _control.IsEnabled = false; };
            viewer.OnConnectionTerminated += (_, _) => { SetStatus("학생 화면 연결 종료"); _control.IsEnabled = false; };
            ((ISupportInitialize)viewer).BeginInit();
            _surface = new ViewerZoomSurface();
            _host.Child = _surface;
            _surface.Controls.Add(viewer);
            ((ISupportInitialize)viewer).EndInit();
            viewer.CreateControl();
            _surface.Attach(viewer);
            viewer.OnSharedRectChanged += (_, e) => _surface?.SetSourceSize(e.right - e.left, e.bottom - e.top);
            viewer.OnSharedDesktopSettingsChanged += (_, e) => _surface?.SetSourceSize(e.width, e.height);
            viewer.SmartSizing = true;
            SetStatus("학생 화면 연결 중");
            _viewerKey = delivery.Invitation.StudentId;
            _viewerStudent = delivery.Student;
            _viewerInvitationId = delivery.Invitation.InvitationId;
            _usedInvitations.Add(delivery.Invitation.InvitationId);
            _releasedConnections.Remove(delivery.Student);
            _connection = _reception.Watch(delivery.Invitation.StudentId, viewer,
                delivery.Invitation.ConnectionString, delivery.Invitation.ProfessorId, delivery.Secret.Password);
        }
        catch (Exception ex) { SetStatus("학생 화면 오류: " + ex.GetType().Name); _control.IsEnabled = false; }
        finally { _lifecycle.Release(); }
    }

    private async Task ChangeControlAsync()
    {
        if (_connection?.IsConnectionLive != true || _delivery is null) return;
        _control.IsEnabled = false;
        try
        {
            var manager = Model.SessionManager;
            if (manager.CurrentControlState is { Phase: EduStream.Core.Collaboration.ControlPhase.Active } current && current.Student == _delivery.Student)
                await manager.StopControlAsync();
            else await manager.RequestControlAsync(StudentName);
            var active = manager.CurrentControlState is { Phase: EduStream.Core.Collaboration.ControlPhase.Active } state && state.Student == _delivery.Student;
            _control.Content = active ? "제어 중지" : "원격 제어";
            if (_surface is not null) _surface.WheelZoomEnabled = !active; // 제어 중에는 휠을 학생 PC에 전달한다.
            SetStatus(active ? "원격 제어 중 · 학생 화면에 마우스/키보드 입력" : "보기 전용 · 제어 비활성");
        }
        catch (Exception ex) { SetStatus("제어 확인 실패: " + ex.GetType().Name); }
        finally { _control.IsEnabled = _connection?.IsConnectionLive == true; }
    }

    /// <summary>카드의 "원격 제어" 버튼: 카드의 뷰어를 별도 창으로 옮겨 크게 보여 준다. 이미 열려 있으면 앞으로 가져온다.</summary>
    private async Task OpenWindowAsync()
    {
        if (_stopped) return;
        if (_large is not null) { _large.Activate(); return; }
        _root.Children.Remove(_host);
        _host.Height = double.NaN;
        _windowLayout.Children.Add(_host);
        var window = new Window
        {
            Title = StudentName + " · 학생 화면", Width = 1000, Height = 700, Content = _windowLayout,
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BrushBgPrimary"),
        };
        window.SourceInitialized += (_, _) => CaptureExclusion.Apply(window);
        window.Closed += (_, _) =>
        {
            // 창을 닫으면 뷰어를 카드 미리보기로 되돌린다. 연결은 끊지 않는다.
            window.Content = null;
            _windowLayout.Children.Remove(_host);
            _host.Height = 180;
            if (!_root.Children.Contains(_host)) _root.Children.Insert(1, _host);
            if (ReferenceEquals(_large, window)) _large = null;
        };
        _large = window;
        window.Show();
        if (_delivery is { } delivery && _connection?.IsConnectionLive != true) await ConnectAsync(delivery);
    }

    /// <summary>
    /// 새로고침: 연결이 살아 있으면 화면만 다시 그리고 연결은 건드리지 않는다.
    /// 끊긴 상태면 다시 붙는다. 학생 앱은 한 번 쓴 초대를 다시 쓰지 않고 몇 초 안에 새 초대를 보내므로,
    /// 이미 쓴 초대로 붙으면 흰 화면만 뜬다. 그래서 새 초대가 오면 그것으로 연결한다.
    /// </summary>
    private async Task RefreshViewAsync()
    {
        if (_connection?.IsConnectionLive == true) { _viewer?.Refresh(); _surface?.Fit(); return; }
        if (_stopped || _delivery is null) { SetStatus("학생 화면 공유 연결 대기"); return; }
        var latest = _router?.TryGetInvitation(_delivery.Student.ConnectionId) ?? _delivery;
        if (_usedInvitations.Contains(latest.Invitation.InvitationId))
        {
            _delivery = latest;
            SetStatus("학생 쪽 새 초대를 기다리는 중입니다. 몇 초 안에 자동으로 연결됩니다.");
            return;
        }
        await ConnectAsync(latest, retry: true);
    }

    private async Task ClearAsync()
    {
        await _lifecycle.WaitAsync();
        try { await ReleaseAsync(); _delivery = null; SetStatus("학생 화면 공유 종료"); _large?.Close(); }
        catch (Exception ex) { SetStatus("화면 종료 확인 실패: " + ex.GetType().Name); }
        finally { _lifecycle.Release(); }
    }
    private async Task ReleaseAsync()
    {
        if (_viewerKey is not null) await _reception.ReleaseAsync(_viewerKey);
        if (_viewerStudent is not null) _releasedConnections.Add(_viewerStudent);
        _viewerKey = null; _viewerStudent = null; _viewerInvitationId = null;
        _surface?.Dispose(); _surface = null;
        _host.Child = null; _viewer = null; _connection = null; _control.IsEnabled = false;
    }
    public async Task StopAsync()
    {
        _stopped = true;
        if (_router is not null) { _router.InvitationReady -= Ready; _router.InvitationWithdrawn -= Withdrawn; }
        if (_subscribed) { Model.PropertyChanged -= ModelChanged; Model.SessionManager.ControlStateChanged -= ControlChanged; _subscribed = false; }
        await _lifecycle.WaitAsync();
        try { await ReleaseAsync(); _delivery = null; _large?.Close(); }
        finally { _lifecycle.Release(); }
    }
    public async Task<bool> DisconnectForRevokeAsync(EduStream.Core.Collaboration.ParticipantConnection student, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            if (_releasedConnections.Contains(student)) return true;
            if (_viewerStudent != student) return false;
            await ReleaseAsync(); SetStatus("입력 회수를 위해 학생 화면 연결 종료");
            return true;
        }
        finally { _lifecycle.Release(); }
    }
    public bool HasViewerHistory(EduStream.Core.Collaboration.ParticipantConnection student) =>
        _viewerStudent == student || _releasedConnections.Contains(student);
}
