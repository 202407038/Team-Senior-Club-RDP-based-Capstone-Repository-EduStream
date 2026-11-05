namespace EduStream.Core.Models;

public static class RosterIdentity
{
    // 기존 이름 기반 WDS/파일 경로에서도 동명이인을 구분할 수 있게 같은 표시 식별자를 쓴다.
    public static string ConnectionName(string name, string studentId) =>
        string.IsNullOrWhiteSpace(studentId) ? name.Trim() : $"{name.Trim()} ({studentId.Trim()})";
}
