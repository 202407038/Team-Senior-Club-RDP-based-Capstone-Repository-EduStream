using System.Diagnostics;
using EduStream.Core.Launch;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// U09 2번: 같은 PC에서 학생 앱이 먼저 실행 중이면 교수자 실행을 막고, 교수자 먼저 실행은 학생 실행을 막지 않는지 검증합니다.
/// 테스트마다 별도 표시 이름을 써서 실제 앱 실행이나 다른 테스트와 섞이지 않게 합니다.
/// </summary>
public sealed class LaunchOrderGuardTests
{
    private static LaunchOrderGuard NewGuard() =>
        new($@"Local\EduStream.Test.{Guid.NewGuid():N}");

    [Fact]
    public void NoStudentRunning_ProfessorLaunchAllowed()
    {
        var guard = NewGuard();

        var decision = guard.CheckProfessorLaunch();

        Assert.True(decision.IsAllowed);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void StudentRunning_ProfessorLaunchBlockedWithReason()
    {
        var guard = NewGuard();
        using var student = guard.RegisterStudentInstance();

        var decision = guard.CheckProfessorLaunch();

        Assert.False(decision.IsAllowed);
        Assert.Equal(LaunchOrderGuard.StudentRunningReason, decision.Reason);
    }

    [Fact]
    public void StudentExited_ProfessorLaunchAllowedAgain()
    {
        var guard = NewGuard();
        var student = guard.RegisterStudentInstance();
        Assert.False(guard.CheckProfessorLaunch().IsAllowed);

        student.Dispose();

        Assert.True(guard.CheckProfessorLaunch().IsAllowed);
    }

    [Fact]
    public void MultipleStudents_BlockUntilAllExit()
    {
        var guard = NewGuard();
        var first = guard.RegisterStudentInstance();
        var second = guard.RegisterStudentInstance();

        first.Dispose();
        Assert.False(guard.CheckProfessorLaunch().IsAllowed);

        second.Dispose();
        Assert.True(guard.CheckProfessorLaunch().IsAllowed);
    }

    [Fact]
    public void ProfessorFirst_StudentLaunchStillAllowed()
    {
        var guard = NewGuard();
        Assert.True(guard.CheckProfessorLaunch().IsAllowed);

        // 교수자 앱이 이미 실행 중이어도 학생 등록은 실패하지 않는다.
        using var student = guard.RegisterStudentInstance();

        Assert.True(guard.IsStudentRunning());
    }

    [Fact]
    public async Task StudentProcessKilled_DoesNotLeaveStaleBlock()
    {
        var name = $@"Local\EduStream.Test.{Guid.NewGuid():N}";
        var guard = new LaunchOrderGuard(name);

        // 학생 앱 대신 같은 이름의 표시를 연 채 대기하는 별도 프로세스를 띄우고 강제 종료한다.
        using var process = Process.Start(new ProcessStartInfo
        {
            // PATH에 의존하지 않도록 Windows 기본 설치 경로를 사용한다.
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -Command " +
                $"\"$m = New-Object System.Threading.Mutex($false, '{name}'); Start-Sleep -Seconds 30\"",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        try
        {
            await WaitUntilAsync(guard.IsStudentRunning, TimeSpan.FromSeconds(15));
            Assert.False(guard.CheckProfessorLaunch().IsAllowed);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        await WaitUntilAsync(() => !guard.IsStudentRunning(), TimeSpan.FromSeconds(5));
        Assert.True(guard.CheckProfessorLaunch().IsAllowed);
    }

    [Fact]
    public void EmptyMarkerName_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new LaunchOrderGuard(" "));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("조건이 제한 시간 안에 충족되지 않았습니다.");
    }
}
