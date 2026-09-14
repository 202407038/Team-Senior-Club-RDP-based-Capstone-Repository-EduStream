using System.Windows;
using EduStream.Client.ViewModels;

namespace EduStream.Client;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new ClientViewModel();
        if (DataContext is ClientViewModel vm)
        {
            vm.AttachRdpHost(RdpHost);
        }
    }
}
