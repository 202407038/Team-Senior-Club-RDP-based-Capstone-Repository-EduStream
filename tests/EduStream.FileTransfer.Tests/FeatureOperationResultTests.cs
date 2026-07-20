using EduStream.Core.Factories;
using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.FileTransfer.Tests;

public sealed class FeatureOperationResultTests
{
    [Fact]
    public void FromAck_ShouldMapCommonSuccessContract()
    {
        var sessionId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var packet = PacketFactory.CreateAck(
            senderId: "Server",
            ackCode: AckCodes.FileAccepted,
            message: "파일 전송 완료",
            sessionId: sessionId,
            correlationId: correlationId);

        var result = FeatureOperationResult.FromAck(FeatureArea.File, packet);

        Assert.Equal(FeatureArea.File, result.Feature);
        Assert.Equal(OperationState.Succeeded, result.State);
        Assert.Equal(AckCodes.FileAccepted, result.Code);
        Assert.Equal("파일 전송 완료", result.Message);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Equal(correlationId, result.CorrelationId);
        Assert.True(result.IsSuccess);
        Assert.True(result.IsTerminal);
        Assert.False(result.IsPending);
    }

    [Fact]
    public void FromError_ShouldMapRecoverableFailureContract()
    {
        var packet = PacketFactory.CreateError(
            senderId: "Server",
            errorCode: ErrorCodes.ChecksumMismatch,
            message: "체크섬 검증 실패",
            isRecoverable: true);

        var result = FeatureOperationResult.FromError(FeatureArea.File, packet);

        Assert.Equal(OperationState.Failed, result.State);
        Assert.Equal(ErrorCodes.ChecksumMismatch, result.Code);
        Assert.True(result.IsRecoverable);
        Assert.False(result.IsSuccess);
        Assert.True(result.IsTerminal);
    }

    [Fact]
    public void CreateStatus_ShouldRepresentIndependentFeatureProgress()
    {
        var screen = FeatureOperationResult.CreateStatus(
            FeatureArea.Screen,
            OperationState.InProgress,
            "화면 송신 중",
            progressPercent: 40);
        var file = FeatureOperationResult.CreateStatus(
            FeatureArea.File,
            OperationState.Pending,
            "파일 청크 수신 중",
            progressPercent: 25);
        var chat = FeatureOperationResult.CreateStatus(
            FeatureArea.Chat,
            OperationState.Succeeded,
            "채팅 전송 완료");

        Assert.True(screen.IsPending);
        Assert.True(file.IsPending);
        Assert.True(chat.IsSuccess);
        Assert.Equal(40, screen.ProgressPercent);
        Assert.Equal(25, file.ProgressPercent);
        Assert.NotEqual(screen.CorrelationId, file.CorrelationId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void CreateStatus_ShouldRejectOutOfRangeProgress(int progressPercent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FeatureOperationResult.CreateStatus(
                FeatureArea.File,
                OperationState.InProgress,
                "파일 전송 중",
                progressPercent));
    }

    [Fact]
    public void FromResponse_ShouldRejectUnknownContractCode()
    {
        var packet = new AckPacket
        {
            SenderId = "Server",
            AckCode = "UNKNOWN_ACK",
            Message = "알 수 없는 응답"
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FeatureOperationResult.FromAck(FeatureArea.Session, packet));

        Assert.Equal(ErrorCodes.InvalidAckCode, ex.Message);
    }

    [Fact]
    public void FileReceiveResult_ShouldUseCommonOperationState()
    {
        var success = FileReceiveResult.CreateSuccess("lecture.pdf");
        var pending = FileReceiveResult.CreatePending("파일 청크 수신 중", 2, 4);
        var failure = FileReceiveResult.CreateFailure(ErrorCodes.ChecksumMismatch, "체크섬 불일치");

        Assert.Equal(OperationState.Succeeded, success.State);
        Assert.True(success.Success);
        Assert.False(success.Pending);

        Assert.Equal(OperationState.InProgress, pending.State);
        Assert.False(pending.Success);
        Assert.True(pending.Pending);

        Assert.Equal(OperationState.Failed, failure.State);
        Assert.False(failure.Success);
        Assert.False(failure.Pending);
    }

    [Fact]
    public void FileReceiveResult_ShouldConvertToCommonFeatureResult()
    {
        var sessionId = Guid.NewGuid();
        var failure = FileReceiveResult.CreateFailure(
            ErrorCodes.FileChunkMetadataMismatch,
            "파일 청크 메타데이터 불일치");

        var result = failure.ToOperationResult(sessionId);

        Assert.Equal(FeatureArea.File, result.Feature);
        Assert.Equal(OperationState.Failed, result.State);
        Assert.Equal(ErrorCodes.FileChunkMetadataMismatch, result.Code);
        Assert.Equal("파일 청크 메타데이터 불일치", result.Message);
        Assert.Equal(sessionId, result.SessionId);
        Assert.True(result.IsTerminal);
    }
}
