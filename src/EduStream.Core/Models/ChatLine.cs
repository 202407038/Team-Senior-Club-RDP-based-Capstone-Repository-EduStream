namespace EduStream.Core.Models;

/// <summary>
/// 채팅 목록에 바인딩되는 한 줄 메시지 모델입니다.
/// </summary>
public sealed class ChatLine
{
    public required string Sender { get; init; }

    public required string Message { get; init; }

    public bool IsSystem { get; init; }

    public bool IsSelf { get; init; }

    public static ChatLine System(string message) =>
        new() { Sender = "시스템", Message = message, IsSystem = true };

    public static ChatLine User(string sender, string message, bool isSelf = false) =>
        new() { Sender = sender, Message = message, IsSelf = isSelf };
}
