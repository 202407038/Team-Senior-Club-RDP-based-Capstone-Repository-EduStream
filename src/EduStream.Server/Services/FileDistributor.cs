using System.IO;
using System.Security.Cryptography;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Serialization;

namespace EduStream.Server.Services;

/// <summary>
/// 파일 전송 전 체크섬 생성과 패킷 래핑을 담당합니다.
/// </summary>
public sealed class FileDistributor
{
    private readonly PacketSerializer _serializer;
    private readonly ILogSink _logSink;

    public FileDistributor(PacketSerializer serializer, ILogSink logSink)
    {
        _serializer = serializer;
        _logSink = logSink;
    }

    public async Task<FilePacket> BuildFilePacketAsync(string filePath)
    {
        var content = await File.ReadAllBytesAsync(filePath);
        var packet = new FilePacket
        {
            FileName = Path.GetFileName(filePath),
            FileSize = content.LongLength,
            Content = content,
            Checksum = Convert.ToHexString(SHA256.HashData(content))
        };

        packet.DataLength = _serializer.Serialize(packet).Length;
        _logSink.Write($"파일 패킷 생성: {packet.FileName}, 크기={packet.FileSize} byte");
        return packet;
    }
}
