using System.Buffers.Binary;
using EduStream.Core.Collaboration;

namespace EduStream.Core.Network;

/// <summary>
/// 보호 채널의 4바이트 little-endian 길이 + 본문 프레이밍입니다.
/// 기존 v1 TCP(10MB)와 달리 <see cref="CollaborationMessageCodec.MaxFrameBytes"/>를 상한으로 쓰고,
/// 본문 버퍼를 할당하기 전에 길이를 검사합니다.
/// </summary>
public static class CollaborationFraming
{
    public const int HeaderBytes = 4;

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (frame.Length == 0 || frame.Length > CollaborationMessageCodec.MaxFrameBytes)
            throw new CollaborationException(CollaborationError.ResourceLimit);
        var buffer = new byte[HeaderBytes + frame.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, frame.Length);
        frame.CopyTo(buffer.AsMemory(HeaderBytes));
        await stream.WriteAsync(buffer, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// 프레임 하나를 읽습니다. 프레임 경계에서 상대가 정상 종료했으면 null입니다.
    /// 프레임 중간 종료는 <see cref="IOException"/>, 길이 초과·0은 ResourceLimit입니다.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        await ReadAsync(stream, CollaborationMessageCodec.MaxFrameBytes, cancellationToken);

    /// <param name="maxFrameBytes">핸드셰이크처럼 더 작은 상한이 필요한 단계에서 사용합니다.</param>
    public static async Task<byte[]?> ReadAsync(Stream stream, int maxFrameBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFrameBytes, CollaborationMessageCodec.MaxFrameBytes);
        var header = new byte[HeaderBytes];
        var read = await stream.ReadAtLeastAsync(header, HeaderBytes, throwOnEndOfStream: false, cancellationToken);
        if (read == 0) return null;
        if (read < HeaderBytes) throw new IOException("프레임 길이 헤더 도중 연결이 끊겼습니다.");

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxFrameBytes)
            throw new CollaborationException(CollaborationError.ResourceLimit);

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }
}
