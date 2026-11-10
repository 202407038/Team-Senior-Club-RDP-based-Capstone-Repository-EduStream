using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
    private readonly ClippedWindowsFormsHost _host = new() { Height = 180 };
    private readonly DockPanel _windowLayout = new();
    private readonly StackPanel _root = new();
    private readonly ProfessorReception _reception = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly System.Windows.Controls.Button _control = ThemedButton("원격 제어 시작", enabled: false);
    private readonly TextBlock _hint = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private ReverseCollaborationRouter? _router;
    private ReverseInvitationDelivery? _delivery;
    private AxRDPViewer? _viewer;
    private ViewerZoomSurface? _surface;
    private ProfessorViewerConnection? _connection;
    private Window? _large;
    private bool _loaded;
    private bool _subscribed;
    private ServerViewModel? _subscribedModel;
    private bool _stopped;
    private string? _viewerKey;
    private Guid? _viewerInvitationId;
    private readonly HashSet<Guid> _connectedInvitations = new();
    private string? _captureWarning;
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
        _cardStatus.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
        var open = new System.Windows.Controls.Button
        {
            // 누르면 바로 제어가 시작되지 않고 큰 창이 열린다. 제어는 큰 창의 '원격 제어 시작'으로 시작한다.
            Content = "크게 보기 · 원격 제어", Height = 34, Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "학생 화면을 큰 창으로 엽니다. 창 위쪽의 '원격 제어 시작'을 눌러 조작합니다.",
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

        // 학생 화면 창 상단: 크게 보이는 원격 제어 시작/중지와 다시 연결. 한 번만 만들어 창을 다시 열 때도 재사용한다.
        // 화면은 창 크기에 맞춰 자동으로 맞춰지므로 별도 '화면 맞춤' 버튼은 두지 않는다.
        _control.Click += async (_, _) => await ChangeControlAsync();
        ShowControlButton(active: false);
        var retry = ThemedButton("다시 연결");
        retry.ToolTip = "화면이 멈추거나 연결에 실패했을 때 학생 화면을 다시 연결합니다.";
        retry.Margin = new Thickness(10, 0, 0, 0);
        retry.VerticalAlignment = VerticalAlignment.Center;
        retry.Click += async (_, _) => await RefreshViewAsync();
        _status.FontSize = 13;
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextMuted");
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        _status.Margin = new Thickness(0);
        texts.Children.Add(_status); texts.Children.Add(_hint);
        var bar = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_control, Dock.Left); DockPanel.SetDock(retry, Dock.Right);
        bar.Children.Add(_control); bar.Children.Add(retry); bar.Children.Add(texts);
        var toolbar = new Border
        {
            Child = bar, Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(0, 0, 0, 1),
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
                _subscribedModel = Model;
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
                // 같은 학생이 재입장했더라도 창에서 제거된 이전 템플릿은 정리한다.
                // 단순히 접힌 뷰는 창에 남아 있으므로 연결을 유지한다.
                if (Window.GetWindow(this) is null ||
                    (!IsLoaded && Model is { } model && !model.Participants.Contains(StudentName))) await StopAsync();
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
            ShowControlButton(active);
            SetStatus(state.Phase switch
            {
                EduStream.Core.Collaboration.ControlPhase.Active => "원격 제어 중",
                EduStream.Core.Collaboration.ControlPhase.Requested => "제어 연결 중",
                EduStream.Core.Collaboration.ControlPhase.Failed => "제어 연결 실패",
                _ => ""
            });
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

    private void SetStatus(string text)
    {
        var message = _captureWarning is null ? text : text + " · " + _captureWarning;
        _status.Text = message; _cardStatus.Text = message;
        _status.Visibility = _cardStatus.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Withdrawn(EduStream.Core.Collaboration.ParticipantConnection student, Guid invitationId)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_delivery?.Student == student && _delivery.Invitation.InvitationId == invitationId) await ClearAsync();
        }));
    }

    private async Task ConnectAsync(ReverseInvitationDelivery delivery)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_stopped || _router?.TryGetInvitation(delivery.Student.ConnectionId) != delivery) return;
            // Loaded/IsVisibleChanged/초대 알림이 겹쳐도 연결 중인 동일 뷰어를 끊지 않는다.
            if (_viewerStudent == delivery.Student && _viewerInvitationId == delivery.Invitation.InvitationId &&
                _connection is { Failed: false, Terminated: false } && _viewer is { IsDisposed: false }) return;
            await ReleaseAsync();
            _delivery = delivery;
            if (_connectedInvitations.Contains(delivery.Invitation.InvitationId) || delivery.Invitation.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                SetStatus("학생 쪽 새 초대를 기다리는 중입니다. 초대 갱신 후 자동으로 연결됩니다.");
                return;
            }
            var viewer = new AxRDPViewer();
            _viewer = viewer;
            viewer.OnConnectionEstablished += (_, _) =>
            {
                if (!ReferenceEquals(_viewer, viewer)) return;
                _connectedInvitations.Add(delivery.Invitation.InvitationId);
                SetStatus(""); _control.IsEnabled = true;
            };
            viewer.OnConnectionFailed += (_, _) => { SetStatus("학생 화면 연결 실패 · '다시 연결'을 눌러 주세요."); _control.IsEnabled = false; };
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
            _releasedConnections.Remove(delivery.Student);
            _connection = _reception.Watch(delivery.Invitation.StudentId, viewer,
                delivery.Invitation.ConnectionString, delivery.Invitation.ProfessorId, delivery.Secret.Password);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); SetStatus("학생 화면을 열지 못했습니다 · '다시 연결'을 눌러 주세요."); _control.IsEnabled = false; }
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
            ShowControlButton(active);
            SetStatus(active ? "원격 제어 중" : "");
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); SetStatus("원격 제어 상태를 바꾸지 못했습니다. 다시 눌러 주세요."); }
        finally { _control.IsEnabled = _connection?.IsConnectionLive == true; }
    }

    /// <summary>큰 창을 닫을 때 이 학생에게 걸린 원격 제어(요청 중 포함)를 끝낸다. 다른 학생 제어는 건드리지 않는다.</summary>
    private async Task StopControlOnCloseAsync()
    {
        var student = _viewerStudent ?? _delivery?.Student;
        if (student is null || Model?.SessionManager is not { } manager) return;
        if (manager.CurrentControlState is not { Phase: EduStream.Core.Collaboration.ControlPhase.Active or EduStream.Core.Collaboration.ControlPhase.Requested } state
            || state.Student != student) return;
        try
        {
            await manager.StopControlAsync();
            ShowControlButton(active: false);
            SetStatus("창을 닫아 원격 제어를 끝냈습니다.");
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); SetStatus("원격 제어가 끝났는지 확인하지 못했습니다. 학생 카드에서 다시 확인해 주세요."); }
    }

    private void ShowControlButton(bool active)
    {
        _control.Content = active ? "원격 제어 중지" : "원격 제어 시작";
        _control.Style = (Style)System.Windows.Application.Current.FindResource(active ? "SecondaryButton" : "PrimaryButton");
        _control.MinWidth = 150; _control.Height = 38; _control.FontSize = 14;
        _control.Padding = new Thickness(18, 0, 18, 0); _control.Margin = new Thickness(0);
        _control.ToolTip = active ? "누르면 학생 PC 조작을 멈춥니다." : "누르면 학생 PC를 직접 조작할 수 있습니다.";
        _hint.Text = active ? "창을 닫으면 원격 제어도 자동으로 끝납니다."
            : "'원격 제어 시작'을 눌러야 학생 PC를 조작할 수 있습니다.";
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
        window.SourceInitialized += (_, _) =>
        {
            _captureWarning = CaptureExclusion.TryApply(window, out var error) ? null
                : "캡처 제외 실패: 학생 화면이 재공유될 수 있습니다. 이 창을 공유하지 않는 모니터로 옮겨 주세요.";
            if (_captureWarning is not null)
            {
                Model.ReportCaptureExclusionFailure(error);
                SetStatus("학생 화면 보기");
            }
        };
        window.Closed += (_, _) =>
        {
            // 창을 닫으면 뷰어를 카드 미리보기로 되돌린다. 보기 연결은 끊지 않고, 이 학생 원격 제어만 끝낸다.
            window.Content = null;
            _ = StopControlOnCloseAsync();
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
    /// 최초 연결 실패는 같은 유효 초대로 재시도한다. 연결 성공 뒤 종료된 초대는 재사용하지 않는다.
    /// 연결 중인 뷰어는 유지하며, 학생 측 새 초대 도착은 Ready에서 처리한다.
    /// </summary>
    private async Task RefreshViewAsync()
    {
        if (_stopped || _delivery is null) { SetStatus("학생 화면 공유 연결 대기"); return; }
        var latest = _router?.TryGetInvitation(_delivery.Student.ConnectionId) ?? _delivery;
        var sameInvitation = _viewerInvitationId == latest.Invitation.InvitationId;
        var action = StudentViewerRetryPolicy.Decide(
            sameInvitation && _connection?.IsConnectionLive == true,
            sameInvitation && _connection is { Established: false, Failed: false, Terminated: false } && _viewer is { IsDisposed: false },
            _connectedInvitations.Contains(latest.Invitation.InvitationId), latest.Invitation.ExpiresAt <= DateTimeOffset.UtcNow);
        if (action == StudentViewerRetryAction.Refresh) { _viewer?.Refresh(); _surface?.Fit(); return; }
        if (action == StudentViewerRetryAction.KeepConnecting) { SetStatus("학생 화면 연결 중입니다."); return; }
        if (action == StudentViewerRetryAction.WaitForInvitation)
        {
            // ConnectAsync가 기존 연결을 정리하고 소비/만료 초대의 재사용을 차단한다.
            await ConnectAsync(latest);
            return;
        }
        await ConnectAsync(latest);
    }

    private async Task ClearAsync()
    {
        await _lifecycle.WaitAsync();
        try { await ReleaseAsync(); _delivery = null; SetStatus("학생 화면 공유 종료"); _large?.Close(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); SetStatus("학생 화면이 닫혔는지 확인하지 못했습니다."); }
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
        if (_subscribedModel is { } subscribedModel)
        {
            subscribedModel.PropertyChanged -= ModelChanged;
            subscribedModel.SessionManager.ControlStateChanged -= ControlChanged;
            _subscribedModel = null;
            _subscribed = false;
        }
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
            await ReleaseAsync(); SetStatus("원격 제어를 끝내려고 학생 화면 연결을 닫았습니다.");
            return true;
        }
        finally { _lifecycle.Release(); }
    }
    public bool HasViewerHistory(EduStream.Core.Collaboration.ParticipantConnection student) =>
        _viewerStudent == student || _releasedConnections.Contains(student);
}
