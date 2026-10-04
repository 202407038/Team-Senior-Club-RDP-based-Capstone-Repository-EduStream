using System.Windows;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;
using EduStream.Server.ViewModels;

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
    }

    private bool _closed;
    private bool _closing;

    private void CopyHostIp_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedHostAddress is not { } address)
        {
            MessageBox.Show("사용할 네트워크 주소가 없습니다. LAN 연결을 확인해 주세요.", "EduStream");
            return;
        }
        try { Clipboard.SetText(address.Address); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show("클립보드를 사용할 수 없습니다. 잠시 후 다시 시도해 주세요.", "EduStream");
        }
    }

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
