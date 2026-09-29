using System.Text;
using EduStream.Core.Collaboration;

namespace EduStream.FileTransfer.Tests;

/// <summary>#58/#61의 보호 채널 DTO를 공통 계약 관점에서 검증합니다. 실제 WDS 검증은 아닙니다.</summary>
public sealed class FinalReadinessSecureContractTests
{
    [Fact]
    public void AuthenticationAndReconnect_RoundTripWithStableKinds()
    {
        var request = new RoomAuthRequest
        {
            AttemptId = Guid.NewGuid(), DisplayName = "학생", Password = Encoding.UTF8.GetBytes("room-secret"),
            ReconnectToken = "reconnect-secret"
        };
        var decoded = RoundTrip(request, 8);
        Assert.Equal(request.AttemptId, decoded.AttemptId);
        Assert.Equal(request.DisplayName, decoded.DisplayName);
        Assert.Equal(request.Password, decoded.Password);
        Assert.Equal(request.ReconnectToken, decoded.ReconnectToken);
        var accepted = new RoomAuthResult(request.AttemptId, true, null, "ticket", Guid.NewGuid());
        Assert.Equal(accepted, RoundTrip(accepted, 9));
        var rejected = new RoomAuthResult(request.AttemptId, false, CollaborationError.NotAuthorized, null, null);
        Assert.Equal(rejected, RoundTrip(rejected, 9));
        var grant = new ReconnectGrantNotice("reconnect-secret", 120);
        Assert.Equal(grant, RoundTrip(grant, 12));
        var ended = new SessionEndedNotice(accepted.SessionId!.Value);
        Assert.Equal(ended, RoundTrip(ended, 13));
        Assert.DoesNotContain("room-secret", request.ToString());
        Assert.DoesNotContain("reconnect-secret", request.ToString());
        Assert.DoesNotContain(grant.Token, grant.ToString());
    }

    [Fact]
    public void PermissionAndControlStatus_RoundTripWithStableKinds()
    {
        var session = Guid.NewGuid();
        foreach (var phase in Enum.GetValues<ControlPhase>())
        {
            var notice = new ControlStatusNotice(session, 1, phase);
            Assert.Equal(notice, RoundTrip(notice, 10));
        }
        var permission = new PermissionChangeRequest(Guid.NewGuid(), false, false);
        Assert.Equal(permission, RoundTrip(permission, 11));
    }

    [Fact]
    public void InvitationSecret_PreservesIdentityExpiryAndMasksDiagnostics()
    {
        var notice = ValidSecret();
        Assert.Equal(notice, RoundTrip(notice, 14));
        Assert.DoesNotContain(notice.Password, notice.ToString());
        // codec은 구조를 확인하고, 실제 만료 여부는 소비자가 현재 시각과 대조합니다.
        var expired = notice with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        Assert.Equal(expired, RoundTrip(expired, 14));
        var encoded = CollaborationMessageCodec.Encode(Guid.NewGuid(), notice);
        Assert.Throws<CollaborationException>(() =>
            CollaborationMessageCodec.Decode<RoomAuthResult>(encoded, out _));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("invitation")]
    [InlineData("connection")]
    [InlineData("emptyPassword")]
    [InlineData("longPassword")]
    [InlineData("expiry")]
    public void InvalidSecret_IsRejectedBothBeforeSendAndAfterReceive(string fault)
    {
        var valid = ValidSecret();
        var invalid = fault switch
        {
            "session" => valid with { SessionId = Guid.Empty },
            "invitation" => valid with { InvitationId = Guid.Empty },
            "connection" => valid with { ConnectionId = Guid.Empty },
            "emptyPassword" => valid with { Password = "" },
            "longPassword" => valid with { Password = new string('x', RdpInvitationSecretRules.MaxPasswordLength + 1) },
            _ => valid with { ExpiresAt = default }
        };
        // 로컬 호출의 빈 ID는 인자 오류이며, 수신 디코딩은 공통 계약 오류로 정규화합니다.
        if (fault is "session" or "invitation" or "connection")
            Assert.Throws<ArgumentException>(() => CollaborationMessageCodec.Encode(Guid.NewGuid(), invalid));
        else
            Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Encode(Guid.NewGuid(), invalid));
        var frame = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, MessageId = Guid.NewGuid(), Kind = 14, Payload = invalid
        });
        Assert.Throws<CollaborationException>(() =>
            CollaborationMessageCodec.Decode<RdpInvitationSecretNotice>(frame, out _));
    }

    private static RdpInvitationSecretNotice ValidSecret() => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), "invitation-secret", DateTimeOffset.UtcNow.AddMinutes(5));

    private static T RoundTrip<T>(T payload, int kind) where T : notnull
    {
        var id = Guid.NewGuid();
        var frame = CollaborationMessageCodec.Encode(id, payload);
        using var json = System.Text.Json.JsonDocument.Parse(frame);
        Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(kind, json.RootElement.GetProperty("Kind").GetInt32());
        var result = CollaborationMessageCodec.Decode<T>(frame, out var decodedId);
        Assert.Equal(id, decodedId);
        return result;
    }
}
