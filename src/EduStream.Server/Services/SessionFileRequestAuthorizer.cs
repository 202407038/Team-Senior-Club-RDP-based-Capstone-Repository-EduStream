using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;

namespace EduStream.Server.Services;

/// <summary>
/// 2번 구현: SessionFileCatalog가 청크를 내보내기 전마다 호출하는 실제 인가 게이트입니다.
/// context.Connection을 그대로 신뢰하지 않고 매번 ParticipantRegistry에 다시 대조하며,
/// 레지스트리가 모르는 연결이면 예외 대신 false로 처리합니다(문서 규칙).
/// </summary>
public sealed class SessionFileRequestAuthorizer(ParticipantRegistry registry) : IFileRequestAuthorizer
{
    private readonly ParticipantRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public bool CanDownload(FileDownloadContext context, SessionFileRequest request)
    {
        if (context is null || request is null) return false;

        var claimed = context.Connection;
        if (claimed.Role != ParticipantRole.Student) return false;
        if (claimed.SessionId != request.SessionId) return false;

        var current = _registry.TryResolve(claimed.ConnectionId);
        if (current is null || !current.Connected) return false;

        // 요청이 들고 온 연결 정보가 지금 레지스트리에 살아 있는 것과 정확히 같은지 확인한다.
        // ParticipantId/SessionId/Role 중 하나라도 바뀌었으면 재접속으로 교체된 옛 연결이다.
        return current.Connection == claimed;
    }
}
