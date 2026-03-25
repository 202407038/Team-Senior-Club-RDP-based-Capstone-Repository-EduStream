using System.Windows;
using EduStream.Server.ViewModels;

namespace EduStream.Server;

public partial class MainWindow : Window
{
    private readonly ServerViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new ServerViewModel();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.AttachRdpSurface(RdpPreviewHost);
    }
}
