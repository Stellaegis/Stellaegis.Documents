using System.Collections.Concurrent;

namespace Stellaegis.Documents.InMemory;

/// <summary>
/// In-memory <see cref="IDocumentStore"/> for development and tests. The atomic store methods
/// (<see cref="CreateAsync"/>, <see cref="UpdateCurrentAsync"/>) are trivially atomic here; the
/// PostgreSQL variant wraps them in a transaction.
/// </summary>
internal sealed class InMemoryDocumentStore : IDocumentStore
{
    private readonly ConcurrentDictionary<Guid, Document> _documents = new();
    private readonly ConcurrentDictionary<(Guid Id, int Version), DocumentVersion> _versions = new();

    public Task<Document?> GetAsync(Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        _documents.TryGetValue(documentUniqueId, out var document);
        return Task.FromResult(document);
    }

    public Task<IReadOnlyList<Document>> ListByTenantAsync(string tenantSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSlug);

        var result = _documents.Values
            .Where(d => d.TenantSlug == tenantSlug && d.Status == DocumentStatus.Active)
            .OrderByDescending(d => d.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<Document>>(result);
    }

    public Task CreateAsync(Document document, DocumentVersion? initialVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!_documents.TryAdd(document.DocumentUniqueId, document))
            throw new InvalidOperationException($"Document '{document.DocumentUniqueId:N}' already exists.");

        if (initialVersion is not null)
            _versions[(initialVersion.DocumentUniqueId, initialVersion.VersionNumber)] = initialVersion;

        return Task.CompletedTask;
    }

    public Task UpdateCurrentAsync(Document document, DocumentVersion? newVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        _documents[document.DocumentUniqueId] = document;
        if (newVersion is not null)
            _versions[(newVersion.DocumentUniqueId, newVersion.VersionNumber)] = newVersion;

        return Task.CompletedTask;
    }

    public Task SetStatusAsync(Guid documentUniqueId, DocumentStatus status, string modifiedBy, DateTimeOffset modifiedAt, CancellationToken cancellationToken = default)
    {
        if (_documents.TryGetValue(documentUniqueId, out var document))
            _documents[documentUniqueId] = document with { Status = status, ModifiedBy = modifiedBy, ModifiedAt = modifiedAt };

        return Task.CompletedTask;
    }

    public Task<int> GetMaxVersionNumberAsync(Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        var max = _versions.Keys
            .Where(k => k.Id == documentUniqueId)
            .Select(k => k.Version)
            .DefaultIfEmpty(0)
            .Max();
        return Task.FromResult(max);
    }

    public Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        var result = _versions
            .Where(kv => kv.Key.Id == documentUniqueId)
            .Select(kv => kv.Value)
            .OrderBy(v => v.VersionNumber)
            .ToList();
        return Task.FromResult<IReadOnlyList<DocumentVersion>>(result);
    }

    public Task<DocumentVersion?> GetVersionAsync(Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default)
    {
        _versions.TryGetValue((documentUniqueId, versionNumber), out var version);
        return Task.FromResult(version);
    }
}
