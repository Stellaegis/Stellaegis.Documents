using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stellaegis.Blobs.FileSystem;
using Stellaegis.Documents.PostgreSQL;
using Stellaegis.Primitives;

namespace Stellaegis.Documents.Tests;

/// <summary>
/// A whole document service as a host would register it: the filesystem blob store in a temporary folder
/// of its own, the in-memory or the PostgreSQL store, and a publisher that records the events.
/// </summary>
internal sealed class DocumentsHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    private DocumentsHarness(Action<IServiceCollection, IConfiguration> addStore, IDictionary<string, string?> settings)
    {
        BlobRoot = Path.Combine(Path.GetTempPath(), "stellaegis-documents-tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IEventPublisher>(Events);
        services.AddStellaegisFileSystemBlobStore(o => o.RootPath = BlobRoot);
        addStore(services, configuration);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateAsyncScope();
        Documents = _scope.ServiceProvider.GetRequiredService<IDocumentService>();
    }

    /// <summary>The service under test.</summary>
    public IDocumentService Documents { get; }

    /// <summary>The events the service published.</summary>
    public RecordingEventPublisher Events { get; } = new();

    /// <summary>The folder the blob store writes into.</summary>
    public string BlobRoot { get; }

    /// <summary>A service on the in-memory store.</summary>
    public static DocumentsHarness InMemory(int listPageSize = 100, string? keyPrefix = null) => new(
        (services, configuration) => services.AddStellaegisDocumentsInMemory(configuration),
        keyPrefix is null
            ? new Dictionary<string, string?> { ["Stellaegis:Documents:ListPageSize"] = listPageSize.ToString() }
            : new Dictionary<string, string?>
            {
                ["Stellaegis:Documents:ListPageSize"] = listPageSize.ToString(),
                ["Stellaegis:Documents:KeyPrefix"] = keyPrefix,
            });

    /// <summary>A service on the PostgreSQL store, its file names under the passphrase of <paramref name="activeSlot"/>.</summary>
    public static DocumentsHarness PostgreSql(string connectionString, int activeSlot = 1, int listPageSize = 100) => new(
        (services, configuration) => services.AddStellaegisDocumentsPostgreSQL(configuration),
        new Dictionary<string, string?>
        {
            ["Stellaegis:Documents:ListPageSize"] = listPageSize.ToString(),
            ["Stellaegis:Documents:PostgreSQL:ConnectionString"] = connectionString,
            ["Stellaegis:Documents:PostgreSQL:ActiveKeySlot"] = activeSlot.ToString(),
            ["Stellaegis:Documents:PostgreSQL:Keys:1"] = "first-test-passphrase",
            ["Stellaegis:Documents:PostgreSQL:Keys:2"] = activeSlot == 2 ? "second-test-passphrase" : string.Empty,
        });

    /// <summary>The paths of the blobs on disk, relative to the blob root and with <c>/</c> between segments.</summary>
    public IReadOnlyList<string> BlobPaths()
        => Directory.Exists(BlobRoot)
            ? Directory.GetFiles(BlobRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(BlobRoot, f).Replace(Path.DirectorySeparatorChar, '/')).ToList()
            : [];

    /// <summary>The names of the blobs on disk, whatever their space.</summary>
    public IReadOnlyList<string> BlobNames()
        => Directory.Exists(BlobRoot)
            ? Directory.GetFiles(BlobRoot, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)).ToList()
            : [];

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
        if (Directory.Exists(BlobRoot))
            Directory.Delete(BlobRoot, recursive: true);
    }
}

/// <summary>Content helpers shared by the tests.</summary>
internal static class Content
{
    /// <summary>A compartment no other test uses: the PostgreSQL tests share one database.</summary>
    public static Compartment NewCompartment() => Compartment.Parse("t" + Guid.NewGuid().ToString("N"));

    /// <summary>Actors of the tests.</summary>
    public static Actor Alice { get; } = Actor.Parse("alice");

    /// <inheritdoc cref="Alice"/>
    public static Actor Bob { get; } = Actor.Parse("bob");

    /// <inheritdoc cref="Alice"/>
    public static Actor Mallory { get; } = Actor.Parse("mallory");

    /// <summary>A text content to upload.</summary>
    public static DocumentUpload Upload(string text, string fileName = "file.txt", string contentType = "text/plain", string? expectedHash = null)
        => new()
        {
            Content = new MemoryStream(Encoding.UTF8.GetBytes(text)),
            FileName = fileName,
            ContentType = contentType,
            ExpectedContentHash = expectedHash,
        };

    /// <summary>The SHA-256 of a text, as the module keeps it.</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Reads a stream to its end as text, and disposes it.</summary>
    public static async Task<string> ReadAsync(Stream stream)
    {
        await using (stream)
        {
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>Records the events instead of sending them.</summary>
internal sealed class RecordingEventPublisher : IEventPublisher
{
    private readonly List<IEvent> _published = [];

    public IReadOnlyList<IEvent> Published
    {
        get { lock (_published) return [.. _published]; }
    }

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IEvent
    {
        lock (_published)
            _published.Add(@event);
        return Task.CompletedTask;
    }
}

/// <summary>A content of any size produced as it is read: it never exists whole.</summary>
internal sealed class GeneratedContent(long size, byte seed = 0) : Stream
{
    /// <summary>The bytes produced so far.</summary>
    public long Produced { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => size;

    public override long Position
    {
        get => Produced;
        set => throw new NotSupportedException();
    }

    /// <summary>The SHA-256 of the whole content, computed by producing it once more.</summary>
    public static string HashOf(long size, byte seed = 0)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var content = new GeneratedContent(size, seed);
        var buffer = new byte[81920];
        int read;
        while ((read = content.Read(buffer)) > 0)
            hash.AppendData(buffer.AsSpan(0, read));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, size - Produced);
        for (var i = 0; i < count; i++)
            buffer[i] = (byte)((Produced + i) * 31 + seed);
        Produced += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Task.FromResult(Read(buffer, offset, count));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
