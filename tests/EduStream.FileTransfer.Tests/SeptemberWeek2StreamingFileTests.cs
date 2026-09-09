using EduStream.Client.Services;
using EduStream.Server.Services;
using EduStream.Core.Logging;
using EduStream.Core.Serialization;
using EduStream.Core.Utils;

namespace EduStream.FileTransfer.Tests;

public sealed class SeptemberWeek2StreamingFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EduStream-September", Guid.NewGuid().ToString("N"));
    public SeptemberWeek2StreamingFileTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(0)]
    [InlineData(65537)]
    [InlineData(8 * 1024 * 1024)]
    public async Task StreamedChunks_RoundTripWithSessionAndChecksum(int size)
    {
        var data = AugustFileTestSupport.CreateContent(size);
        var source = Path.Combine(_directory, "source.bin");
        await File.WriteAllBytesAsync(source, data);
        var distributor = new FileDistributor(new PacketSerializer(), new InMemoryLogSink());
        var receiver = new FileReceiver();
        var session = Guid.NewGuid();
        var destination = Path.Combine(_directory, "received");
        var count = 0;
        await foreach (var packet in distributor.StreamFilePacketsAsync(source, "Professor", session))
        {
            Assert.Equal(session, packet.SessionId);
            Assert.True(packet.Content.Length <= 65536);
            var result = await receiver.TrySaveAsync(packet, destination);
            Assert.True(result.Pending || result.Success);
            count++;
        }
        Assert.True(count >= 1);
        Assert.Equal(ChecksumUtility.ComputeSha256(data),
            ChecksumUtility.ComputeSha256(await File.ReadAllBytesAsync(Path.Combine(destination, "source.bin"))));
    }

    [Fact]
    public async Task CancelAfterFirstChunk_ReleasesSourceFile()
    {
        var path = Path.Combine(_directory, "cancel.bin");
        await File.WriteAllBytesAsync(path, new byte[128 * 1024]);
        using var cts = new CancellationTokenSource();
        var distributor = new FileDistributor(new PacketSerializer(), new InMemoryLogSink());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var packet in distributor.StreamFilePacketsAsync(path, "Professor", Guid.NewGuid(),
                cancellationToken: cts.Token))
                cts.Cancel();
        });
        using var writable = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        Assert.True(writable.CanWrite);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
