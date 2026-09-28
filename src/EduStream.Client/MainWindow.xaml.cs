using System.Windows;
using EduStream.Client.ViewModels;

namespace EduStream.Client;

public partial class MainWindow : Window
{
    private bool _shutdownComplete;
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
        }
        Closing += async (_, e) =>
        {
            if (_shutdownComplete) return;
            e.Cancel = true;
            IsEnabled = false;
            try { if (DataContext is ClientViewModel model) await model.ShutdownAsync(); }
            finally { _shutdownComplete = true; Close(); }
        };
    }

    private async void ConnectRdp_Click(object sender, RoutedEventArgs e)
    {
        var password = RdpPassword.Password;
        RdpPassword.Clear();
        if (DataContext is ClientViewModel vm) await vm.ConnectRdpWithPasswordAsync(password);
    }
}
