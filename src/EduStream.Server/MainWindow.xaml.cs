using System.Windows;
using EduStream.Server.ViewModels;

namespace EduStream.Server;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new ServerViewModel();
    }
}
