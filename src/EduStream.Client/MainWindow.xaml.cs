using System.ComponentModel;
using System.Windows;
using EduStream.Client.ViewModels;

namespace EduStream.Client;

public partial class MainWindow : Window
{
    private readonly ClientViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new ClientViewModel();
        _viewModel.PropertyChanged += OnViewModelChanged;
        DataContext = _viewModel;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ClientViewModel.IsConnected)) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (WindowState != WindowState.Normal) return;

            bool connected = _viewModel.IsConnected;

            MinWidth = connected ? 960 : 480;
            MinHeight = connected ? 700 : 600;
            Width = connected ? 1360 : 520;
            Height = connected ? 820 : 740;

            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }));
    }
}