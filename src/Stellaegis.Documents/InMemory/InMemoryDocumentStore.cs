using System.Collections.Concurrent;
using Stellaegis.Primitives;

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

    public Task<Document?> GetAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default)
        => Task.FromResult(Find(holder, documentUniqueId));

    public Task<IReadOnlyList<Document>> ListAsync(string holder, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(holder);

        var result = _documents.Values
            .Where(d => d.Holder == holder && d.Status == DocumentStatus.Active)
            .OrderByDescending(d => d.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<Document>>(result);
    }

    public Task CreateAsync(Document document, DocumentVersion? initialVersion, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(document);

        if (!_documents.TryAdd(document.DocumentUniqueId, document))
            throw new InvalidOperationException($"Document '{document.DocumentUniqueId:N}' already exists.");

        if (initialVersion is not null)
            _versions[(initialVersion.DocumentUniqueId, initialVersion.VersionNumber)] = initialVersion;

        return Task.CompletedTask;
    }

    public Task UpdateCurrentAsync(Document document, DocumentVersion? newVersion, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(document);

        if (Find(document.Holder, document.DocumentUniqueId) is null)
            return Task.CompletedTask;

        _documents[document.DocumentUniqueId] = document;
        if (newVersion is not null)
            _versions[(newVersion.DocumentUniqueId, newVersion.VersionNumber)] = newVersion;

        return Task.CompletedTask;
    }

    public Task SetStatusAsync(string holder, Guid documentUniqueId, DocumentStatus status, string modifiedBy, DateTimeOffset modifiedAt, CancellationToken cancellationToken = default)
    {
        if (Find(holder, documentUniqueId) is { } document)
            _documents[documentUniqueId] = document with { Status = status, ModifiedBy = modifiedBy, ModifiedAt = modifiedAt };

        return Task.CompletedTask;
    }

    public Task<int> GetMaxVersionNumberAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        var max = Versions(holder, documentUniqueId)
            .Select(v => v.VersionNumber)
            .DefaultIfEmpty(0)
            .Max();
        return Task.FromResult(max);
    }

    public Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        var result = Versions(holder, documentUniqueId)
            .OrderBy(v => v.VersionNumber)
            .ToList();
        return Task.FromResult<IReadOnlyList<DocumentVersion>>(result);
    }

    public Task<DocumentVersion?> GetVersionAsync(string holder, Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default)
    {
        var version = Find(holder, documentUniqueId) is not null
            && _versions.TryGetValue((documentUniqueId, versionNumber), out var found)
            ? found
            : null;
        return Task.FromResult(version);
    }

    private Document? Find(string holder, Guid documentUniqueId)
    {
        Guard.NotNull(holder);
        return _documents.TryGetValue(documentUniqueId, out var document) && document.Holder == holder ? document : null;
    }

    private IEnumerable<DocumentVersion> Versions(string holder, Guid documentUniqueId)
        => Find(holder, documentUniqueId) is null
            ? []
            : _versions.Where(kv => kv.Key.Id == documentUniqueId).Select(kv => kv.Value);
}
