using System.Text;
using EduStream.Client.Services;
using EduStream.Core.Logging;
using EduStream.Core.Models;
using EduStream.Core.Protocols;
using EduStream.Core.Serialization;
using EduStream.Core.Utils;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class FileReceiverDay6Tests
{
    [Fact]
    public async Task SmallFile_RoundTrip_WithDistributorAndReceiver_ShouldSucceed()
    {
        var sourceDirectory = CreateIsolatedDirectory();
        var targetDirectory = CreateIsolatedDirectory();
        var sourcePath = Path.Combine(sourceDirectory, "small-source.txt");
        var sourceText = "day4-small-roundtrip-" + new string('s', 200);
        await File.WriteAllTextAsync(sourcePath, sourceText);

        var distributor = new FileDistributor(new PacketSerializer(), new InMemoryLogSink());
        var receiver = new FileReceiver();

        var packets = await distributor.BuildFilePacketsAsync(sourcePath, FileTransferRules.MinChunkSize);
        Assert.True(packets.Count >= 1);

        FileReceiveResult? last = null;
        foreach (var packet in packets)
        {
            last = await receiver.TrySaveAsync(packet, targetDirectory);
        }

        Assert.NotNull(last);
        Assert.True(last!.Success);
        Assert.False(last.Pending);
        Assert.NotNull(last.FilePath);
        Assert.True(File.Exists(last.FilePath));
        Assert.Equal(sourceText, await File.ReadAllTextAsync(last.FilePath!));
    }

    [Fact]
    public async Task MediumFile_RoundTrip_WithDistributorAndReceiver_ShouldSucceed()
    {
        var sourceDirectory = CreateIsolatedDirectory();
        var targetDirectory = CreateIsolatedDirectory();
        var sourcePath = Path.Combine(sourceDirectory, "medium-source.bin");

        // 256KB 고정 데이터로 중간 크기 파일 왕복 검증
        var sourceBytes = Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);

        var distributor = new FileDistributor(new PacketSerializer(), new InMemoryLogSink());
        var receiver = new FileReceiver();

        var packets = await distributor.BuildFilePacketsAsync(sourcePath, FileTransferRules.MinChunkSize);
        Assert.True(packets.Count > 1);

        FileReceiveResult? last = null;
        foreach (var packet in packets)
        {
            last = await receiver.TrySaveAsync(packet, targetDirectory);
        }

        Assert.NotNull(last);
        Assert.True(last!.Success);
        Assert.False(last.Pending);
        Assert.NotNull(last.FilePath);
        Assert.True(File.Exists(last.FilePath));

        var savedBytes = await File.ReadAllBytesAsync(last.FilePath!);
        Assert.Equal(sourceBytes.Length, savedBytes.Length);
        Assert.Equal(ChecksumUtility.ComputeSha256(sourceBytes), ChecksumUtility.ComputeSha256(savedBytes));
    }

    [Fact]
    public async Task ChunkedFile_InvalidChunkIndex_ShouldFailWithErrorCode()
    {
        var receiver = new FileReceiver();
        var targetDirectory = CreateIsolatedDirectory();
        var bytes = Encoding.UTF8.GetBytes("invalid-index-case");
        var packet = CreatePacket(
            content: bytes,
            fileName: "invalid-index.txt",
            transferId: Guid.NewGuid(),
            chunkIndex: 2,
            totalChunks: 2,
            checksum: ChecksumUtility.ComputeSha256(bytes),
            fileSize: bytes.Length);

        var result = await receiver.TrySaveAsync(packet, targetDirectory);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidChunkIndex, result.ErrorCode);
    }

    [Fact]
    public async Task ChunkedFile_Failure_ShouldNotLeaveTemporaryFiles()
    {
        var receiver = new FileReceiver();
        var targetDirectory = CreateIsolatedDirectory();
        var fullContent = Encoding.UTF8.GetBytes("tmp-cleanup-policy-check");
        var transferId = Guid.NewGuid();
        var checksum = ChecksumUtility.ComputeSha256(fullContent);

        var firstPacket = CreatePacket(
            content: fullContent[..10],
            fileName: "cleanup-a.txt",
            transferId: transferId,
            chunkIndex: 0,
            totalChunks: 2,
            checksum: checksum,
            fileSize: fullContent.Length);

        var secondPacket = CreatePacket(
            content: fullContent[10..],
            fileName: "cleanup-b.txt",
            transferId: transferId,
            chunkIndex: 1,
            totalChunks: 2,
            checksum: checksum,
            fileSize: fullContent.Length);

        var first = await receiver.TrySaveAsync(firstPacket, targetDirectory);
        var second = await receiver.TrySaveAsync(secondPacket, targetDirectory);

        Assert.True(first.Pending);
        Assert.False(second.Success);

        var tempFiles = Directory.GetFiles(targetDirectory, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task SingleFile_ShouldSaveSuccessfully_Repeated()
    {
        var receiver = new FileReceiver();
        var targetDirectory = CreateIsolatedDirectory();

        for (var round = 1; round <= 2; round++)
        {
            var text = $"single-day6-{round}-" + new string('A', 64);
            var bytes = Encoding.UTF8.GetBytes(text);
            var packet = CreatePacket(
                content: bytes,
                fileName: $"single-{round}.txt",
                transferId: Guid.NewGuid(),
                chunkIndex: 0,
                totalChunks: 1,
                checksum: ChecksumUtility.ComputeSha256(bytes),
                fileSize: bytes.Length);

            var result = await receiver.TrySaveAsync(packet, targetDirectory);

            Assert.True(result.Success);
            Assert.False(result.Pending);
            Assert.NotNull(result.FilePath);
            Assert.True(File.Exists(result.FilePath));
            Assert.Equal(text, await File.ReadAllTextAsync(result.FilePath!));
        }
    }

    [Fact]
    public async Task ChunkedFile_ShouldReturnPendingThenSuccess_Repeated()
    {
        var receiver = new FileReceiver();
        var targetDirectory = CreateIsolatedDirectory();

        for (var round = 1; round <= 3; round++)
        {
            var contentText = $"chunk-day6-{round}-" + string.Join('|', Enumerable.Range(0, 30));
            var fullContent = Encoding.UTF8.GetBytes(contentText);
            var checksum = ChecksumUtility.ComputeSha256(fullContent);
            var transferId = Guid.NewGuid();
            var chunkSize = 13;
            var totalChunks = (int)Math.Ceiling(fullContent.Length / (double)chunkSize);

            var pendingCount = 0;
            FileReceiveResult? lastResult = null;

            for (var index = 0; index < totalChunks; index++)
            {
                var length = Math.Min(chunkSize, fullContent.Length - (index * chunkSize));
                var chunk = new byte[length];
                Array.Copy(fullContent, index * chunkSize, chunk, 0, length);

                var packet = CreatePacket(
                    content: chunk,
                    fileName: $"chunk-{round}.txt",
                    transferId: transferId,
                    chunkIndex: index,
                    totalChunks: totalChunks,
                    checksum: checksum,
                    fileSize: fullContent.Length);

                lastResult = await receiver.TrySaveAsync(packet, targetDirectory);
                if (lastResult.Pending)
                {
                    pendingCount++;
                }
            }

            Assert.NotNull(lastResult);
            Assert.True(lastResult!.Success);
            Assert.False(lastResult.Pending);
            Assert.True(pendingCount >= 1);
            Assert.NotNull(lastResult.FilePath);
            Assert.True(File.Exists(lastResult.FilePath));
            Assert.Equal(contentText, await File.ReadAllTextAsync(lastResult.FilePath!));
        }
    }

    [Fact]
    public async Task ChunkedFile_MetadataMismatch_ShouldFail()
    {
        var receiver = new FileReceiver();
        var targetDirectory = CreateIsolatedDirectory();
        var fullContent = Encoding.UTF8.GetBytes("metadata-mismatch-day6");
        var transferId = Guid.NewGuid();
        var checksum = ChecksumUtility.ComputeSha256(fullContent);

        var firstPacket = CreatePacket(
            content: fullContent[..10],
            fileName: "mismatch-a.txt",
            transferId: transferId,
            chunkIndex: 0,
            totalChunks: 2,
            checksum: checksum,
            fileSize: fullContent.Length);

        var secondPacket = CreatePacket(
            content: fullContent[10..],
            fileName: "mismatch-b.txt",
            transferId: transferId,
            chunkIndex: 1,
            totalChunks: 2,
            checksum: checksum,
            fileSize: fullContent.Length);

        var firstResult = await receiver.TrySaveAsync(firstPacket, targetDirectory);
        var secondResult = await receiver.TrySaveAsync(secondPacket, targetDirectory);

        Assert.True(firstResult.Pending);
        Assert.False(secondResult.Success);
        Assert.Equal(ErrorCodes.FileChunkMetadataMismatch, secondResult.ErrorCode);
    }

    private static string CreateIsolatedDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "EduStream-Day6-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static FilePacket CreatePacket(byte[] content, string fileName, Guid transferId, int chunkIndex, int totalChunks, string checksum, long fileSize)
    {
        return new FilePacket
        {
            SenderId = "day6-test",
            SessionId = Guid.NewGuid(),
            FileName = fileName,
            FileSize = fileSize,
            Checksum = checksum,
            TransferId = transferId,
            ChunkIndex = chunkIndex,
            TotalChunks = totalChunks,
            Content = content
        };
    }
}