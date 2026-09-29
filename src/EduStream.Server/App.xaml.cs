using System.Configuration;
using System.Data;
using System.Windows;
using EduStream.Core.Launch;

namespace EduStream.Server;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // U09: 같은 PC에서 학생 앱이 먼저 실행 중이면 교수자 앱을 열지 않는다.
        var decision = new LaunchOrderGuard().CheckProfessorLaunch();
        if (!decision.IsAllowed)
        {
            System.Windows.MessageBox.Show(decision.Reason, "EduStream 교수자 실행 불가",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }
}
