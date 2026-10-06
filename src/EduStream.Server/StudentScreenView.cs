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
    private readonly TextBlock _status = new() { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap };
    private readonly WindowsFormsHost _host = new() { Height = 180 };
    private readonly StackPanel _root = new();
    private readonly ProfessorReception _reception = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly System.Windows.Controls.Button _control = new() { Content = "원격 제어", IsEnabled = false, Margin = new Thickness(3) };
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
    private EduStream.Core.Collaboration.ParticipantConnection? _viewerStudent;
    private readonly HashSet<EduStream.Core.Collaboration.ParticipantConnection> _releasedConnections = new();
    private string StudentName => DataContext as string ?? string.Empty;
    public bool ShowingStudentScreen => !_stopped && (IsVisible || _large?.IsVisible == true) && _viewer is not null;
    public Guid? ConnectedSharingFor(EduStream.Core.Collaboration.ParticipantConnection student) =>
        _viewerStudent == student && _connection?.IsConnectionLive == true ? _delivery?.Invitation.SharingId : null;

    public StudentScreenView()
    {
        var actions = new WrapPanel();
        var large = new System.Windows.Controls.Button { Content = "크게 보기", Margin = new Thickness(3) };
        large.Click += (_, _) => Enlarge();
        _control.Click += async (_, _) => await ChangeControlAsync();
        var retry = new System.Windows.Controls.Button { Content = "다시 연결", Margin = new Thickness(3) };
        retry.Click += async (_, _) => { if (_delivery is { } d) await ConnectAsync(d, retry: true); };
        actions.Children.Add(large); actions.Children.Add(_control); actions.Children.Add(retry);
        var fit = new System.Windows.Controls.Button { Content = "화면 맞춤", Margin = new Thickness(3) };
        fit.Click += (_, _) => _surface?.Fit(); actions.Children.Add(fit);
        _root.Children.Add(_status); _root.Children.Add(_host); _root.Children.Add(actions);
        Content = _root;
        IsVisibleChanged += async (_, _) =>
        {
            if (!_stopped && IsVisible && _delivery is { } delivery)
            {
                if (Model.IsRdpSharing) await Model.StopRdpShareAsync();
                if (_connection?.IsConnectionLive != true) await ConnectAsync(delivery);
            }
        };
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
            _status.Text = active ? "원격 제어 중" : "보기 전용 · 제어 " + state.Phase;
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
        _status.Text = "학생 화면 공유 연결 대기";
    }

    private void Ready(ReverseInvitationDelivery delivery)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_stopped) return;
            if (delivery.DisplayName != StudentName) return;
            if (ReferenceEquals(_delivery, delivery) && _connection?.IsConnectionLive == true) return;
            _delivery = delivery;
            if (_loaded && IsVisible) await ConnectAsync(delivery);
        }));
    }

    private void Withdrawn(EduStream.Core.Collaboration.ParticipantConnection student, Guid invitationId)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_delivery?.Student == student && _delivery.Invitation.InvitationId == invitationId) await ClearAsync();
        }));
    }

    private async Task ConnectAsync(ReverseInvitationDelivery delivery, bool retry = false)
    {
        // 공유 종료 과정에서 입력 회수가 이 뷰어의 수명 잠금을 필요로 할 수 있다.
        // 잠금을 잡은 채 공유 종료를 기다리면 서로 기다리는 교착 상태가 된다.
        if (Model.IsRdpSharing) await Model.StopRdpShareAsync();
        await _lifecycle.WaitAsync();
        try
        {
            if (_stopped || _router?.TryGetInvitation(delivery.Student.ConnectionId) != delivery) return;
            // Loaded/IsVisibleChanged/초대 알림이 겹쳐도 연결 중인 동일 뷰어를 끊지 않는다.
            if (!retry && _viewerStudent == delivery.Student && _viewerInvitationId == delivery.Invitation.InvitationId &&
                _connection is { Failed: false, Terminated: false } && _viewer is { IsDisposed: false }) return;
            // 창 캡처 제외를 WDS가 보장하지 않으므로 학생 화면을 열기 전에 정방향 공유를 중지한다.
            if (Model.IsRdpSharing) throw new InvalidOperationException("교수자 공유 종료 확인이 필요합니다.");
            await ReleaseAsync();
            _delivery = delivery;
            var viewer = new AxRDPViewer();
            _viewer = viewer;
            viewer.OnConnectionEstablished += (_, _) => { _status.Text = "학생 화면 연결됨 · 보기 전용"; _control.IsEnabled = true; };
            viewer.OnConnectionFailed += (_, _) => { _status.Text = "학생 화면 연결 실패 · 다시 연결을 눌러 주세요."; _control.IsEnabled = false; };
            viewer.OnConnectionTerminated += (_, _) => { _status.Text = "학생 화면 연결 종료"; _control.IsEnabled = false; };
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
            _status.Text = "학생 화면 연결 중";
            _viewerKey = delivery.Invitation.StudentId;
            _viewerStudent = delivery.Student;
            _viewerInvitationId = delivery.Invitation.InvitationId;
            _releasedConnections.Remove(delivery.Student);
            _connection = _reception.Watch(delivery.Invitation.StudentId, viewer,
                delivery.Invitation.ConnectionString, delivery.Invitation.ProfessorId, delivery.Secret.Password);
        }
        catch (Exception ex) { _status.Text = "학생 화면 오류: " + ex.GetType().Name; _control.IsEnabled = false; }
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
            _status.Text = active ? "원격 제어 중 · 학생 화면에 마우스/키보드 입력" : "보기 전용 · 제어 비활성";
        }
        catch (Exception ex) { _status.Text = "제어 확인 실패: " + ex.GetType().Name; }
        finally { _control.IsEnabled = _connection?.IsConnectionLive == true; }
    }

    private void Enlarge()
    {
        if (_large is not null) { _large.Activate(); return; }
        _root.Children.Remove(_host);
        _host.Height = double.NaN;
        _large = new Window { Title = StudentName + " · 학생 화면", Width = 1000, Height = 700, Content = _host, Background = Brushes.Black };
        _large.Closed += (_, _) => { _large.Content = null; _large = null; _host.Height = 180; _root.Children.Insert(1, _host); };
        _large.Show();
    }

    private async Task ClearAsync()
    {
        await _lifecycle.WaitAsync();
        try { await ReleaseAsync(); _delivery = null; _status.Text = "학생 화면 공유 종료"; _large?.Close(); }
        catch (Exception ex) { _status.Text = "화면 종료 확인 실패: " + ex.GetType().Name; }
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
            await ReleaseAsync(); _status.Text = "입력 회수를 위해 학생 화면 연결 종료";
            return true;
        }
        finally { _lifecycle.Release(); }
    }
    public bool HasViewerHistory(EduStream.Core.Collaboration.ParticipantConnection student) =>
        _viewerStudent == student || _releasedConnections.Contains(student);
}
