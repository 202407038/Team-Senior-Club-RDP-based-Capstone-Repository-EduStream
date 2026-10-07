namespace EduStream.Server;

internal enum StudentViewerRetryAction { Refresh, KeepConnecting, WaitForInvitation, Connect }

internal static class StudentViewerRetryPolicy
{
    // 연결 시도 자체는 초대 소비가 아니다. 실제 연결 성공 뒤에만 재사용을 막는다.
    internal static StudentViewerRetryAction Decide(bool live, bool connecting, bool establishedBefore, bool expired)
    {
        if (live) return StudentViewerRetryAction.Refresh;
        if (connecting) return StudentViewerRetryAction.KeepConnecting;
        if (establishedBefore || expired) return StudentViewerRetryAction.WaitForInvitation;
        return StudentViewerRetryAction.Connect;
    }
}
