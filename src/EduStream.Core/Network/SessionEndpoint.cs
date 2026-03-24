namespace EduStream.Core.Network;

/// <summary>
/// 세션 접속 정보를 주소와 포트 단위로 표현하는 공통 모델입니다.
/// </summary>
public sealed class SessionEndpoint
{
    public string Address { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 5000;

    public override string ToString() => $"{Address}:{Port}";
}
