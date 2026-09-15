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
