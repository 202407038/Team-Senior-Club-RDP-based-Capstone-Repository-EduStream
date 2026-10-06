using System.Text;
using EduStream.Core.Collaboration;

namespace EduStream.FileTransfer.Tests;

/// <summary>
/// 2번 담당: Kind 18(교수자→학생 실제 입력 허용/회수 명령)과 Kind 19(학생→교수자 적용 결과) 공통 계약.
/// 번호 고정, 왕복, 다른 Kind로의 해석 거부, 필수 값·성공/오류 일관성 검사를 확인합니다.
/// </summary>
public sealed class RemoteInputContractTests
{
    [Fact]
    public void Kinds_ArePinnedAfterAnnotation()
    {
        Assert.Equal(18, (int)CollaborationMessageKind.RemoteInputCommand);
        Assert.Equal(19, (int)CollaborationMessageKind.RemoteInputResult);
    }

    [Theory]
    [InlineData(RemoteInputAction.Grant)]
    [InlineData(RemoteInputAction.Revoke)]
    public void Command_RoundTrips(RemoteInputAction action)
    {
        var command = Command() with { Action = action };
        var messageId = Guid.NewGuid();

        var frame = CollaborationMessageCodec.Encode(messageId, command);

        Assert.Equal(CollaborationMessageKind.RemoteInputCommand, CollaborationFrameInspector.PeekKind(frame));
        Assert.Equal(command, CollaborationMessageCodec.Decode<RemoteInputCommandNotice>(frame, out var decodedId));
        Assert.Equal(messageId, decodedId);
    }

    [Fact]
    public void Results_RoundTrip()
    {
        var applied = new RemoteInputResultNotice(Guid.NewGuid(), Guid.NewGuid(), RemoteInputAction.Grant, true, null);
        var failed = new RemoteInputResultNotice(Guid.NewGuid(), Guid.NewGuid(), RemoteInputAction.Grant, false,
            CollaborationError.UnsupportedCapability);

        foreach (var result in new[] { applied, failed })
        {
            var frame = CollaborationMessageCodec.Encode(Guid.NewGuid(), result);
            Assert.Equal(CollaborationMessageKind.RemoteInputResult, CollaborationFrameInspector.PeekKind(frame));
            Assert.Equal(result, CollaborationMessageCodec.Decode<RemoteInputResultNotice>(frame, out _));
        }
    }

    [Fact]
    public void Command_DecodedAsResult_IsRejected()
    {
        var frame = CollaborationMessageCodec.Encode(Guid.NewGuid(), Command());

        var ex = Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Decode<RemoteInputResultNotice>(frame, out _));
        Assert.Equal(CollaborationError.InvalidRequest, ex.Code);
    }

    public static TheoryData<string> InvalidCommands => new()
    {
        "empty-command", "empty-session", "empty-sharing", "empty-control-request", "blank-professor", "long-professor", "undefined-action"
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void InvalidCommand_IsRejectedOnEncodeAndDecode(string defect)
    {
        var valid = Command();
        var invalid = defect switch
        {
            "empty-command" => valid with { CommandId = Guid.Empty },
            "empty-session" => valid with { SessionId = Guid.Empty },
            "empty-sharing" => valid with { SharingId = Guid.Empty },
            "empty-control-request" => valid with { ControlRequestId = Guid.Empty },
            "blank-professor" => valid with { ProfessorId = " " },
            "long-professor" => valid with { ProfessorId = new string('p', ReverseRdpInvitationNotice.MaxIdentityLength + 1) },
            "undefined-action" => valid with { Action = (RemoteInputAction)99 },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };

        // 송신 측은 기존 규칙대로 빈 ID를 ArgumentException, 나머지를 InvalidRequest로 막는다.
        var encodeError = Assert.ThrowsAny<Exception>(() => CollaborationMessageCodec.Encode(Guid.NewGuid(), invalid));
        Assert.True(encodeError is ArgumentException or CollaborationException { Code: CollaborationError.InvalidRequest });
        Assert.Equal(CollaborationError.InvalidRequest,
            Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Decode<RemoteInputCommandNotice>(
                Forge(CollaborationMessageKind.RemoteInputCommand, invalid), out _)).Code);
    }

    public static TheoryData<bool, int?> InconsistentResults => new()
    {
        { true, (int)CollaborationError.PermissionDenied },
        { false, null },
        { false, 99 }
    };

    [Theory]
    [MemberData(nameof(InconsistentResults))]
    public void InconsistentResult_IsRejected(bool applied, int? error)
    {
        var result = new RemoteInputResultNotice(Guid.NewGuid(), Guid.NewGuid(), RemoteInputAction.Revoke, applied,
            (CollaborationError?)error);

        Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Encode(Guid.NewGuid(), result));
        Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Decode<RemoteInputResultNotice>(
            Forge(CollaborationMessageKind.RemoteInputResult, result), out _));
    }

    private static RemoteInputCommandNotice Command() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "professor-id", RemoteInputAction.Grant);

    // 송신 측 검증을 우회한 프레임. 수신 측 Decode도 같은 규칙으로 거부해야 한다.
    private static byte[] Forge<T>(CollaborationMessageKind kind, T payload) where T : notnull =>
        Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new CollaborationEnvelope(
            CollaborationContract.Version, Guid.NewGuid(), kind, System.Text.Json.JsonSerializer.SerializeToElement(payload))));
}
