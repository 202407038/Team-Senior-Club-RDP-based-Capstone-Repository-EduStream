using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EduStream.Core.Collaboration;

namespace EduStream.ShareHost;

/// <summary>
/// 학생(호스트) → 교수자(뷰어) 역방향 초대의 JSON 전송 계약.
///
/// 왜 별도 계약인가:
///  - Core의 <c>RdpInvitationPacket</c>/<c>RdpInvitationContract</c>는 교수자→학생 "보기 전용" 정방향 전용입니다.
///    (<c>ViewOnly == false</c>이면 Validate가 거부합니다. 이 동작은 1번 소유 Core 계약이며 변경하지 않습니다.)
///  - 역방향은 ProfessorId / StudentId / InvitationId 등 정방향 패킷에 없는 필드가 필요하고,
///    호스트(학생)가 허용했을 때만 제어(ControlLevel=3)로 올라갈 수 있는 별도 의미를 가집니다.
///  - 그래서 정방향 패킷에 <c>ViewOnly=false</c>를 끼워 넣거나 Dictionary로 억지 변환하지 않고,
///    모든 필드를 명시적으로 매핑하는 전용 DTO를 사용합니다.
///
/// 모든 식별자는 <c>required</c> 이므로 JSON에서 하나라도 빠지면 역직렬화 단계에서 실패하고,
/// 빈 GUID·불일치 값은 <see cref="Validate"/> 에서 접속 이전에 거부됩니다.
/// 초대 비밀번호는 이 DTO에 포함하지 않으며 별도 보호 경로로 전달해야 합니다.
/// </summary>
public sealed class ReverseInvitationWire
{
    public const int CurrentContractVersion = ReverseRdpInvitationNotice.CurrentVersion;
    public const string ProviderName = ReverseRdpInvitationNotice.ProviderName;
    public const string StudentToProfessorDirection = ReverseRdpInvitationNotice.StudentToProfessor;
    public const int MaximumConnectionStringBytes = ReverseRdpInvitationNotice.MaxConnectionStringBytes;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public required int ContractVersion { get; init; }
    public required string Provider { get; init; }
    public required string Direction { get; init; }

    public required Guid SessionId { get; init; }
    public required Guid SharingId { get; init; }
    public required Guid InvitationId { get; init; }
    public required Guid ConnectionId { get; init; }

    /// <summary>접속이 승인되어야 하는 교수자 (뷰어 쪽 신원)</summary>
    public required string ProfessorId { get; init; }

    /// <summary>화면을 공유하는 학생 (호스트 쪽 신원)</summary>
    public required string StudentId { get; init; }

    /// <summary>뷰어가 WDS Connect 시 사용하는 이름. 역방향에서는 항상 ProfessorId 와 같아야 합니다.</summary>
    public required string ParticipantId { get; init; }

    public required string ConnectionString { get; init; }
    public required int DataLength { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    public required ReverseControlMode ControlMode { get; init; }

    /// <summary>
    /// ControlMode가 ViewOnly인지 여부(읽는 쪽 편의를 위해 명시 직렬화).
    /// 역방향 기본 계약(HostGrantedInteractive)에서는 false 이며 ControlMode 와 모순되면 Validate가 거부합니다.
    /// </summary>
    public required bool ViewOnly { get; init; }

    /// <summary>역방향 초대 패킷 → 전송 DTO. 모든 필드를 명시적으로 매핑합니다.</summary>
    public static ReverseInvitationWire From(ReverseInvitationPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        return new ReverseInvitationWire
        {
            ContractVersion = CurrentContractVersion,
            Provider = ProviderName,
            Direction = StudentToProfessorDirection,
            SessionId = packet.SessionId,
            SharingId = packet.SharingId,
            InvitationId = packet.InvitationId,
            ConnectionId = packet.ConnectionId,
            ProfessorId = packet.ProfessorId,
            StudentId = packet.HostStudentId,
            ParticipantId = packet.ProfessorId,
            ConnectionString = packet.ConnectionString,
            DataLength = Encoding.UTF8.GetByteCount(packet.ConnectionString ?? string.Empty),
            ExpiresAt = packet.ExpiresAt,
            ControlMode = packet.ControlMode,
            ViewOnly = packet.ControlMode == ReverseControlMode.ViewOnly
        };
    }

    /// <summary>전송 DTO → 역방향 초대 패킷. 값 손실 없이 명시적으로 복원합니다.</summary>
    public ReverseInvitationPacket ToPacket() => new()
    {
        SessionId = SessionId,
        SharingId = SharingId,
        InvitationId = InvitationId,
        ConnectionId = ConnectionId,
        ProfessorId = ProfessorId,
        HostStudentId = StudentId,
        ConnectionString = ConnectionString,
        ExpiresAt = ExpiresAt,
        ControlMode = ControlMode
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>기존 엔진 DTO와 공통 보호 채널 계약 사이의 무손실 매핑입니다. 라우팅은 수행하지 않습니다.</summary>
    public ReverseRdpInvitationNotice ToContract() => new()
    {
        ContractVersion = ContractVersion, Provider = Provider, Direction = Direction,
        SessionId = SessionId, SharingId = SharingId, InvitationId = InvitationId, ConnectionId = ConnectionId,
        ProfessorId = ProfessorId, StudentId = StudentId, ParticipantId = ParticipantId,
        ConnectionString = ConnectionString, DataLength = DataLength, ExpiresAt = ExpiresAt,
        ControlMode = (ReverseRdpControlMode)ControlMode, ViewOnly = ViewOnly
    };

    public static ReverseInvitationWire FromContract(ReverseRdpInvitationNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        notice.Validate();
        return new ReverseInvitationWire
        {
            ContractVersion = notice.ContractVersion, Provider = notice.Provider, Direction = notice.Direction,
            SessionId = notice.SessionId, SharingId = notice.SharingId, InvitationId = notice.InvitationId,
            ConnectionId = notice.ConnectionId, ProfessorId = notice.ProfessorId, StudentId = notice.StudentId,
            ParticipantId = notice.ParticipantId, ConnectionString = notice.ConnectionString,
            DataLength = notice.DataLength, ExpiresAt = notice.ExpiresAt,
            ControlMode = (ReverseControlMode)notice.ControlMode, ViewOnly = notice.ViewOnly
        };
    }

    /// <summary>
    /// JSON → DTO. 필수 항목이 하나라도 없으면 <see cref="JsonException"/> 이 발생합니다.
    /// (값의 의미 검증은 <see cref="Validate"/> 로 별도 수행)
    /// </summary>
    public static ReverseInvitationWire FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("역방향 초대 JSON이 비어 있습니다.", nameof(json));

        return JsonSerializer.Deserialize<ReverseInvitationWire>(json, JsonOptions)
            ?? throw new JsonException("역방향 초대 JSON을 해석할 수 없습니다.");
    }

    /// <summary>
    /// 뷰어(교수자) 접속 직전 검증. Core 정방향 계약과 같은 엄격함(버전/공급자/식별자/만료/길이)을 적용하되,
    /// 보기 전용이 아닌 "호스트 허용형 제어"를 정당한 역방향 계약으로 인정합니다.
    /// </summary>
    public void Validate(Guid expectedSessionId, string expectedProfessorId, Guid expectedConnectionId,
        DateTimeOffset now, string? expectedStudentId = null)
    {
        ToContract().ValidateForConnection(expectedSessionId, expectedProfessorId, expectedConnectionId,
            expectedStudentId ?? StudentId, now);
    }
}
