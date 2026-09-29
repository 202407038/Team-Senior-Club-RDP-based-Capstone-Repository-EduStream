using System.Configuration;
using System.Data;
using System.Windows;
using EduStream.Core.Launch;
using Application = System.Windows.Application;

namespace EduStream.Client;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IDisposable? _studentLaunchRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        // U09: 학생 앱 실행 중임을 표시해 교수자 앱이 뒤늦게 실행되지 않게 한다. 학생 실행 자체는 막지 않는다.
        _studentLaunchRegistration = new LaunchOrderGuard().RegisterStudentInstance();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _studentLaunchRegistration?.Dispose();
        _studentLaunchRegistration = null;
        base.OnExit(e);
    }
}
