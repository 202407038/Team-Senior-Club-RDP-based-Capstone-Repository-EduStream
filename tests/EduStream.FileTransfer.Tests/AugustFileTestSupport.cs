using EduStream.Core.Models;

namespace EduStream.FileTransfer.Tests;

internal static class AugustFileTestSupport
{
    public static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EduStream-August-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static string CreateFile(string fileName, byte[] content)
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    public static byte[] CreateContent(int size)
    {
        return Enumerable.Range(0, size).Select(index => (byte)(index % 251)).ToArray();
    }

    public static FilePacket CreatePacket(
        byte[] content,
        string fileName,
        Guid transferId,
        int chunkIndex,
        int totalChunks,
        string checksum,
        long fileSize)
    {
        return new FilePacket
        {
            SenderId = "august-file-test",
            SessionId = Guid.NewGuid(),
            FileName = fileName,
            FileSize = fileSize,
            Checksum = checksum,
            TransferId = transferId,
            ChunkIndex = chunkIndex,
            TotalChunks = totalChunks,
            Content = content,
            DataLength = content.Length
        };
    }

    public static FilePacket[] CreateTwoPackets(
        byte[] content,
        string fileName,
        Guid transferId,
        string checksum)
    {
        var split = content.Length / 2;
        return
        [
            CreatePacket(content[..split], fileName, transferId, 0, 2, checksum, content.Length),
            CreatePacket(content[split..], fileName, transferId, 1, 2, checksum, content.Length)
        ];
    }
}
