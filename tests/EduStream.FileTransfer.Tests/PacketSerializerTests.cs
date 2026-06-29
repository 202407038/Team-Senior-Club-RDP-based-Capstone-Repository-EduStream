using System.Text;
using EduStream.Core.Factories;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Core.Utils;

namespace EduStream.FileTransfer.Tests;

public sealed class PacketSerializerTests
{
    [Fact]
    public void Serialize_ShouldUseCompactJsonForNetworkPayload()
    {
        var serializer = new PacketSerializer();
        var packet = PacketFactory.CreateAck("server", AckCodes.FileAccepted, "파일 전송을 시작합니다.");

        var payload = serializer.Serialize(packet);
        var json = Encoding.UTF8.GetString(payload);

        Assert.DoesNotContain(Environment.NewLine, json);
        Assert.Contains("\"MessageType\":6", json);
    }

    [Fact]
    public void Deserialize_FilePacket_ShouldPreservePayloadContract()
    {
        var serializer = new PacketSerializer();
        var transferId = Guid.NewGuid();
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var packet = PacketFactory.CreateFileChunk(
            senderId: "Server",
            fileName: "week4.bin",
            fileSize: content.Length,
            checksum: "checksum",
            transferId: transferId,
            chunkIndex: 0,
            totalChunks: 1,
            content: content,
            sessionId: Guid.NewGuid());

        var payload = serializer.Serialize(packet);
        var roundTrip = serializer.Deserialize<FilePacket>(payload);

        Assert.NotNull(roundTrip);
        Assert.Equal(PacketType.File, roundTrip!.MessageType);
        Assert.Equal(transferId, roundTrip.TransferId);
        Assert.Equal(content.Length, roundTrip.DataLength);
        Assert.Equal(content, roundTrip.Content);
    }

    [Fact]
    public void ValidatePayloadLength_ShouldUseActualScreenPayloadLength()
    {
        var packet = PacketFactory.CreateScreenFrame(
            senderId: "server",
            frameIndex: 1,
            frameDescription: "frame",
            width: 1280,
            height: 720,
            encoding: ScreenEncodings.Png,
            content: [1, 2, 3]);

        PacketContractUtility.ValidatePayloadLength(packet.DataLength, packet.ContentLength);

        packet.DataLength += 1;
        var ex = Assert.Throws<InvalidOperationException>(
            () => PacketContractUtility.ValidatePayloadLength(packet.DataLength, packet.ContentLength));

        Assert.Equal(ErrorCodes.PayloadLengthMismatch, ex.Message);
    }

    [Fact]
    public void ValidatePayloadLength_ShouldUseActualFilePayloadLength()
    {
        var packet = PacketFactory.CreateFileChunk(
            senderId: "server",
            fileName: "lecture.bin",
            fileSize: 4,
            checksum: "checksum",
            transferId: Guid.NewGuid(),
            chunkIndex: 0,
            totalChunks: 1,
            content: [1, 2, 3, 4]);

        PacketContractUtility.ValidatePayloadLength(packet.DataLength, packet.ContentLength);

        packet.DataLength -= 1;
        var ex = Assert.Throws<InvalidOperationException>(
            () => PacketContractUtility.ValidatePayloadLength(packet.DataLength, packet.ContentLength));

        Assert.Equal(ErrorCodes.PayloadLengthMismatch, ex.Message);
    }
}
