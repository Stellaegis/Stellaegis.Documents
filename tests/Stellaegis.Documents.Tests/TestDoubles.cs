using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Stellaegis.Backplane;
using Stellaegis.Blobs;
using Stellaegis.MultiTenant.Context;

namespace Stellaegis.Documents.Tests;

/// <summary>
/// Minimal <see cref="ITenantContext"/> stub with a fixed ambient slug (or none).
/// </summary>
internal sealed class FakeTenantContext(string? tenantSlug) : ITenantContext
{
    public string? TenantSlug { get; private set; } = tenantSlug;

    public bool HasTenant => TenantSlug is not null;

    public void SetTenant(string tenantSlug) => TenantSlug = tenantSlug;

    public void Clear() => TenantSlug = null;

    public bool TryGetTenantSlug([NotNullWhen(true)] out string? tenantSlug)
    {
        tenantSlug = TenantSlug;
        return tenantSlug is not null;
    }
}

/// <summary>
/// In-memory <see cref="IBlobStore"/> that records the last read location and serves seeded content,
/// so tests can assert which blob space a resolution targeted.
/// </summary>
internal sealed class RecordingBlobStore : IBlobStore
{
    private readonly Dictionary<(string Slug, string Name, string Key), byte[]> _content = new();

    public BlobLocation? LastReadLocation { get; private set; }

    public void Put(BlobLocation location, string content)
        => _content[Key(location)] = Encoding.UTF8.GetBytes(content);

    public Task<Stream> OpenReadAsync(BlobLocation location, CancellationToken cancellationToken = default)
    {
        LastReadLocation = location;
        if (_content.TryGetValue(Key(location), out var bytes))
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));

        throw new BlobNotFoundException(location);
    }

    public Task WriteAsync(BlobLocation location, Stream content, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        _content[Key(location)] = ms.ToArray();
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(BlobLocation location, CancellationToken cancellationToken = default)
        => Task.FromResult(_content.ContainsKey(Key(location)));

    public Task DeleteAsync(BlobLocation location, CancellationToken cancellationToken = default)
    {
        _content.Remove(Key(location));
        return Task.CompletedTask;
    }

    public Task<Uri?> TryGetDownloadUrlAsync(BlobLocation location, TimeSpan validity, CancellationToken cancellationToken = default)
        => Task.FromResult<Uri?>(null);

    private static (string, string, string) Key(BlobLocation l) => (l.TenantSlug, l.Name, l.Key);
}

/// <summary>
/// <see cref="IBackplane"/> double that records every published event, so tests can assert the
/// document domain emits the expected indexing/trashing events.
/// </summary>
internal sealed class RecordingBackplane : IBackplane
{
    public List<IBackplaneEvent> Published { get; } = [];

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IBackplaneEvent
    {
        Published.Add(@event);
        return Task.CompletedTask;
    }

    public void Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IBackplaneEvent { }

    public void SubscribeRaw(string eventType, Func<string?, JsonElement, CancellationToken, Task> handler, string? tenantId = null) { }
}
