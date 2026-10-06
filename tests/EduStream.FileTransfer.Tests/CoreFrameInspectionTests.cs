using System.Text;
using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;

namespace EduStream.FileTransfer.Tests;

public sealed class CoreFrameInspectionTests
{
    [Theory]
    [InlineData("trailing")]
    [InlineData("duplicate-kind")]
    [InlineData("duplicate-payload")]
    [InlineData("version")]
    [InlineData("message")]
    [InlineData("payload")]
    public void DispatchInspection_RejectsMalformedEnvelopeBeforeSelectingHandler(string fault)
    {
        var id = Guid.NewGuid();
        var frame = Encoding.UTF8.GetString(CollaborationMessageCodec.Encode(id,
            new FileCancelRequest(Guid.NewGuid(), Guid.NewGuid())));
        frame = fault switch
        {
            "trailing" => frame + "{}",
            "duplicate-kind" => frame.Insert(1, "\"Kind\":15,"),
            "duplicate-payload" => frame.Insert(1, "\"Payload\":{},"),
            "version" => frame.Replace("\"Version\":1", "\"Version\":99"),
            "message" => frame.Replace(id.ToString(), Guid.Empty.ToString()),
            _ => frame[..frame.IndexOf("\"Payload\":")] + "\"Payload\":null}"
        };
        var error = Assert.Throws<CollaborationException>(() =>
            CollaborationFrameInspector.PeekKind(Encoding.UTF8.GetBytes(frame)));
        Assert.Equal(fault == "version" ? CollaborationError.UnsupportedCapability :
            CollaborationError.InvalidRequest, error.Code);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void DispatchInspection_AllowsNewKindsButDoesNotReplacePayloadValidation(int kind)
    {
        var frame = Encoding.UTF8.GetBytes(
            $"{{\"Version\":1,\"MessageId\":\"{Guid.NewGuid()}\",\"Kind\":{kind},\"Payload\":{{}}}}");
        Assert.Equal((CollaborationMessageKind)kind, CollaborationFrameInspector.PeekKind(frame));
        // Kind 식별 성공은 본문 승인이나 실제 기능 성공이 아닙니다.
        if (kind == 15) Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Decode<ReverseRdpInvitationNotice>(frame, out _));
        if (kind == 16) Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Decode<ReverseRdpInvitationSecretNotice>(frame, out _));
        if (kind == 17) Assert.Throws<CollaborationException>(() => CollaborationMessageCodec.Decode<AnnotationTransportNotice>(frame, out _));
    }

    [Fact]
    public void FrameWriteOrViewerTimeout_IsRetryableAndDoesNotExposeExceptionDetails()
    {
        var failure = CollaborationErrorCatalog.FromException(new TimeoutException("secret-invitation C:\\private"));
        Assert.Equal("OPERATION_TIMEOUT", failure.Code);
        Assert.True(failure.CanRetry);
        Assert.DoesNotContain("secret-invitation", failure.UserMessage);
        Assert.DoesNotContain("private", failure.UserMessage);
    }
}
