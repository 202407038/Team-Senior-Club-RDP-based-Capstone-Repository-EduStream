namespace EduStream.Core.Collaboration;

/// <summary>
/// 인증된 상대 한 명에게 <see cref="CollaborationMessageCodec"/> 프레임을 보내는 송신 접점입니다.
/// 구현은 동시 호출을 직렬화해 프레임 단위 순서를 보장하고, 수신 쪽은 길이를 메모리 할당 전에 제한해야 합니다.
/// </summary>
/// <remarks>
/// 현재 단계에서는 보호 채널(TLS·capability 협상) 계약이 확정되지 않아 실제 네트워크 구현이 없습니다.
/// 평문 v1 TCP로 대체 연결하지 않습니다.
/// </remarks>
public interface ICollaborationChannel
{
    Task SendAsync(byte[] frame, CancellationToken cancellationToken = default);
}
