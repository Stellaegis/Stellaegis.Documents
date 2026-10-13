namespace Stellaegis.Documents.Tests;

/// <summary>Runs alone: allocations are counted for the whole process.</summary>
[CollectionDefinition(nameof(MemoryMeasures), DisableParallelization = true)]
public sealed class MemoryMeasures;

/// <summary>
/// Contents pass through the service a few buffers at a time: a content of any size never sits whole in
/// memory.
/// </summary>
[Collection(nameof(MemoryMeasures))]
public sealed class StreamingTests
{
    private const long Size = 64L * 1024 * 1024;
    private const long Budget = 8L * 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(T Result, long Allocated)> MeasureAsync<T>(Func<Task<T>> action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var result = await action();
        return (result, GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    private static DocumentUpload Generated(byte seed) => new()
    {
        Content = new GeneratedContent(Size, seed),
        FileName = "big.bin",
        ContentType = "application/octet-stream",
    };

    [Fact]
    public async Task Storing_replacing_and_reading_a_large_content_allocate_a_few_buffers()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = Content.NewHolder();

        var (created, onCreate) = await MeasureAsync(() => harness.Documents.CreateAsync(holder, "archive", Generated(1), "alice", cancellationToken: Ct));
        var (updated, onUpdate) = await MeasureAsync(() => harness.Documents.UpdateContentAsync(holder, created.DocumentUniqueId, Generated(2), "alice", Ct));
        var (hash, onRead) = await MeasureAsync(async () =>
        {
            await using var stream = await harness.Documents.OpenContentAsync(holder, created.DocumentUniqueId, Ct);
            return Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, Ct));
        });

        Assert.Equal((Size, GeneratedContent.HashOf(Size, 1)), (created.ContentLength, created.ContentHash));
        Assert.Equal(GeneratedContent.HashOf(Size, 2), updated.ContentHash);
        Assert.Equal(updated.ContentHash, hash);
        Assert.True(onCreate < Budget, $"Storing {Size} bytes allocated {onCreate}.");
        Assert.True(onUpdate < Budget, $"Replacing {Size} bytes allocated {onUpdate}.");
        Assert.True(onRead < Budget, $"Reading {Size} bytes allocated {onRead}.");
    }
}
