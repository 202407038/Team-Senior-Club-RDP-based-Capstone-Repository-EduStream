using System.Text.Json;
using EduStream.Core.Collaboration;

namespace EduStream.Core.Network;

/// <summary>TLS 연결 직후 서로 한 번씩 보내는 capability 선언입니다. 인증 정보는 담지 않습니다.</summary>
public sealed record CollaborationHello(string Capability, int Version, ParticipantRole Role);

/// <summary>
/// 보호 채널 capability 협상. 학생이 먼저 보내고, 교수자가 검증 후 응답합니다.
/// 어느 쪽이든 capability·버전·역할이 맞지 않으면 연결을 닫습니다(구버전으로 낮춰 계속하지 않습니다).
/// </summary>
public static class CollaborationHandshake
{
    // capability 선언은 작다. 핸드셰이크 단계에서 큰 프레임을 받지 않는다.
    public const int MaxHelloBytes = 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 4 };

    public static CollaborationHello For(ParticipantRole role) =>
        new(CollaborationContract.Capability, CollaborationContract.Version, role);

    public static Task SendAsync(Stream stream, ParticipantRole role, CancellationToken cancellationToken) =>
        CollaborationFraming.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(For(role), Options), cancellationToken);

    /// <summary>상대의 선언을 읽고 기대한 역할인지까지 확인합니다.</summary>
    public static async Task ReceiveAsync(Stream stream, ParticipantRole expectedPeerRole, CancellationToken cancellationToken)
    {
        var frame = await CollaborationFraming.ReadAsync(stream, MaxHelloBytes, cancellationToken)
            ?? throw new CollaborationException(CollaborationError.UnsupportedCapability);
        Validate(frame, expectedPeerRole);
    }

    public static void Validate(ReadOnlySpan<byte> frame, ParticipantRole expectedPeerRole)
    {
        if (frame.Length > MaxHelloBytes) throw new CollaborationException(CollaborationError.ResourceLimit);
        CollaborationHello? hello;
        try
        {
            hello = JsonSerializer.Deserialize<CollaborationHello>(frame, Options);
        }
        catch (JsonException)
        {
            throw new CollaborationException(CollaborationError.UnsupportedCapability);
        }
        if (hello is null || hello.Capability != CollaborationContract.Capability ||
            hello.Version != CollaborationContract.Version)
            throw new CollaborationException(CollaborationError.UnsupportedCapability);
        if (hello.Role != expectedPeerRole)
            throw new CollaborationException(CollaborationError.NotAuthorized);
    }
}
