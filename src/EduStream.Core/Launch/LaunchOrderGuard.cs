namespace EduStream.Core.Launch;

/// <summary>교수자 앱 실행 가능 여부와, 막혔을 때 사용자에게 보여 줄 이유입니다.</summary>
public sealed record LaunchOrderDecision(bool IsAllowed, string? Reason)
{
    public static LaunchOrderDecision Allowed { get; } = new(true, null);
}

/// <summary>
/// 2번 구현(U09): 같은 PC에서 학생 앱이 먼저 실행 중이면 교수자 앱 실행을 막습니다.
/// 교수자 앱을 먼저 실행한 경우 학생 앱 실행은 허용합니다.
/// </summary>
/// <remarks>
/// 학생 앱은 실행 중 이름 있는 커널 객체(Mutex) 핸들을 열어 두고, 교수자 앱은 시작 시 존재 여부만 확인합니다.
/// 소유권(lock)을 잡지 않으므로 학생 앱 여러 개가 동시에 표시를 유지할 수 있습니다.
/// - 비정상 종료: 프로세스가 끝나면 OS가 핸들을 닫으므로 표시가 남지 않습니다.
/// - 다른 Windows 로그인: "Local\" 이름이라 로그인 세션별로 분리됩니다. 다른 사용자 세션의 학생 앱은 막지 않습니다.
/// - 동시 시작: 교수자 확인이 학생 표시 생성보다 먼저 끝나면 허용되며, 이는 "교수자 먼저 실행"과 같은 결과입니다.
/// 현재 단계에서는 Windows 전용 동작이며, 다른 OS에서는 항상 허용합니다.
/// </remarks>
public sealed class LaunchOrderGuard
{
    public const string DefaultStudentMarkerName = @"Local\EduStream.Student.Running";

    public const string StudentRunningReason =
        "이 PC에서 학생 앱이 이미 실행 중입니다. 교수자 앱은 학생 앱보다 먼저 실행해야 합니다.\n" +
        "학생 앱을 모두 종료한 뒤 교수자 앱을 다시 실행해 주세요.";

    private readonly string _studentMarkerName;

    /// <param name="studentMarkerName">테스트 격리용입니다. 앱에서는 기본값을 사용합니다.</param>
    public LaunchOrderGuard(string studentMarkerName = DefaultStudentMarkerName)
    {
        if (string.IsNullOrWhiteSpace(studentMarkerName))
            throw new ArgumentException("학생 실행 표시 이름이 필요합니다.", nameof(studentMarkerName));
        _studentMarkerName = studentMarkerName;
    }

    /// <summary>같은 로그인 세션에서 학생 앱이 실행 중이면 true입니다.</summary>
    public bool IsStudentRunning()
    {
        if (!OperatingSystem.IsWindows()) return false;

        if (!Mutex.TryOpenExisting(_studentMarkerName, out var marker)) return false;
        marker.Dispose();
        return true;
    }

    /// <summary>교수자 앱 시작 시 호출합니다. 막히면 Reason을 사용자에게 안내하고 종료합니다.</summary>
    public LaunchOrderDecision CheckProfessorLaunch() =>
        IsStudentRunning() ? new LaunchOrderDecision(false, StudentRunningReason) : LaunchOrderDecision.Allowed;

    /// <summary>
    /// 학생 앱 시작 시 호출하고, 반환값을 앱 종료 시까지 보관했다가 Dispose합니다.
    /// 교수자 앱 실행 여부와 관계없이 학생 앱 실행은 막지 않습니다.
    /// </summary>
    public IDisposable RegisterStudentInstance()
    {
        if (!OperatingSystem.IsWindows()) return NoopRegistration.Instance;

        // initiallyOwned=false: 소유권 없이 핸들만 유지해 여러 학생 인스턴스가 공존하게 한다.
        return new Mutex(initiallyOwned: false, _studentMarkerName);
    }

    private sealed class NoopRegistration : IDisposable
    {
        public static NoopRegistration Instance { get; } = new();
        public void Dispose() { }
    }
}
