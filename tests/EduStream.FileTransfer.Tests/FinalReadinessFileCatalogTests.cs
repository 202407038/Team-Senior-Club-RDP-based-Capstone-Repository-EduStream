using EduStream.Core.Collaboration;
using EduStream.Core.FileSharing;
using EduStream.Server.Services;

namespace EduStream.FileTransfer.Tests;

public sealed class FinalReadinessFileCatalogTests
{
    [Fact]
    public async Task Register_DoesNotSendBytesAndDoesNotExposeLocalPath()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var source = await fixture.SourceAsync(1024);
        var file = await catalog.RegisterAsync(source);
        var snapshot = catalog.GetSnapshot();
        Assert.Single(snapshot.Files);
        Assert.Equal(1, snapshot.Revision);
        Assert.DoesNotContain(fixture.Root, System.Text.Json.JsonSerializer.Serialize(snapshot));
        Assert.True(catalog.Unregister(file.FileId));
        Assert.False(catalog.Unregister(file.FileId));
        Assert.Empty(catalog.GetSnapshot().Files);
        Assert.Equal(2, catalog.GetSnapshot().Revision);
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("connection")]
    [InlineData("session")]
    [InlineData("role")]
    public async Task UnauthorizedRequest_DoesNotProduceAnyChunks(string reason)
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var file = await catalog.RegisterAsync(await fixture.SourceAsync(10));
        var context = fixture.Context;
        if (reason == "permission") fixture.Authorizer.Allowed = false;
        if (reason == "connection") context = new(context.Connection with { ConnectionId = Guid.NewGuid() });
        if (reason == "session") context = new(context.Connection with { SessionId = Guid.NewGuid() });
        if (reason == "role") context = new(context.Connection with { Role = ParticipantRole.Professor });
        var produced = 0;
        var error = await Assert.ThrowsAsync<CollaborationException>(async () =>
        {
            await foreach (var chunk in catalog.DownloadAsync(context, ReadinessFiles.Request(file))) produced++;
        });
        Assert.Equal(CollaborationError.NotAuthorized, error.Code);
        Assert.Equal(0, produced);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("permission")]
    [InlineData("close")]
    public async Task RevocationBetweenChunks_StopsTransferAndReleasesSource(string action)
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var path = await fixture.SourceAsync(200000);
        var file = await catalog.RegisterAsync(path);
        var count = 0;
        await Assert.ThrowsAsync<CollaborationException>(async () =>
        {
            await foreach (var chunk in catalog.DownloadAsync(fixture.Context, ReadinessFiles.Request(file)))
            {
                count++;
                if (action == "remove") catalog.Unregister(file.FileId);
                if (action == "permission") fixture.Authorizer.Allowed = false;
                if (action == "close") catalog.Dispose();
            }
        });
        Assert.Equal(1, count);
        using var unlocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(unlocked.CanWrite);
    }

    [Fact]
    public async Task ModifiedSource_IsRejectedEvenWhenLengthUnchanged()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var path = await fixture.SourceAsync(10);
        var file = await catalog.RegisterAsync(path);
        await File.WriteAllBytesAsync(path, new byte[10]);
        var error = await Assert.ThrowsAsync<CollaborationException>(async () =>
        {
            await foreach (var chunk in catalog.DownloadAsync(fixture.Context, ReadinessFiles.Request(file))) { }
        });
        Assert.Equal(CollaborationError.SourceChanged, error.Code);
        catalog.Unregister(file.FileId);
        var updated = await catalog.RegisterAsync(path);
        Assert.NotEqual(file.FileId, updated.FileId);
        await foreach (var chunk in catalog.DownloadAsync(fixture.Context, ReadinessFiles.Request(updated)))
            Assert.Equal(new byte[10], chunk.Content);
    }

    [Fact]
    public async Task OldOrWrongFileRevision_IsRejected()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var file = await catalog.RegisterAsync(await fixture.SourceAsync(1));
        var request = ReadinessFiles.Request(file) with { Revision = 2 };
        await Assert.ThrowsAsync<CollaborationException>(async () =>
        {
            await foreach (var chunk in catalog.DownloadAsync(fixture.Context, request)) { }
        });
    }

    [Fact]
    public async Task Cancellation_ReleasesSlotAndSource_RetryUsesNewRequest()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var path = await fixture.SourceAsync(200000);
        var file = await catalog.RegisterAsync(path);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in catalog.DownloadAsync(fixture.Context,
                ReadinessFiles.Request(file), cancellation.Token)) cancellation.Cancel();
        });
        var received = 0;
        await foreach (var chunk in catalog.DownloadAsync(fixture.Context, ReadinessFiles.Request(file))) received++;
        Assert.Equal(file.TotalChunks, received);
    }

    [Fact]
    public async Task DuplicateActiveRequestAndTransferLimit_AreRejectedWithoutLeakingSlots()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var file = await catalog.RegisterAsync(await fixture.SourceAsync(200000));
        var readers = new List<IAsyncEnumerator<SessionFileChunk>>();
        try
        {
            var request = ReadinessFiles.Request(file);
            var first = catalog.DownloadAsync(fixture.Context, request).GetAsyncEnumerator();
            readers.Add(first);
            Assert.True(await first.MoveNextAsync());
            await using (var duplicate = catalog.DownloadAsync(fixture.Context, request).GetAsyncEnumerator())
                await Assert.ThrowsAsync<CollaborationException>(async () => await duplicate.MoveNextAsync());
            for (var i = 1; i < SessionFileLimits.MaxConcurrentTransfers; i++)
            {
                var reader = catalog.DownloadAsync(fixture.Context, ReadinessFiles.Request(file)).GetAsyncEnumerator();
                readers.Add(reader);
                Assert.True(await reader.MoveNextAsync());
            }
            await using var extra = catalog.DownloadAsync(fixture.Context, ReadinessFiles.Request(file)).GetAsyncEnumerator();
            var error = await Assert.ThrowsAsync<CollaborationException>(async () => await extra.MoveNextAsync());
            Assert.Equal(CollaborationError.ResourceLimit, error.Code);
        }
        finally
        {
            foreach (var reader in readers) await reader.DisposeAsync();
        }
        await foreach (var chunk in catalog.DownloadAsync(fixture.Context, ReadinessFiles.Request(file))) { }
    }

    [Fact]
    public async Task CatalogCapacityAndSessionClose_AreEnforced()
    {
        using var fixture = new ReadinessFiles();
        using var catalog = new SessionFileCatalog(fixture.SessionId, fixture.Authorizer);
        var path = await fixture.SourceAsync(0);
        for (var i = 0; i < SessionFileLimits.MaxRegisteredFiles; i++) await catalog.RegisterAsync(path);
        var error = await Assert.ThrowsAsync<CollaborationException>(() => catalog.RegisterAsync(path));
        Assert.Equal(CollaborationError.ResourceLimit, error.Code);
        catalog.Dispose();
        Assert.Throws<CollaborationException>(() => catalog.GetSnapshot());
        await Assert.ThrowsAsync<CollaborationException>(() => catalog.RegisterAsync(path));
    }
}
