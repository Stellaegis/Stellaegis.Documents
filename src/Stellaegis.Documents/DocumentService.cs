using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Backplane;
using Stellaegis.Backplane.DocumentEvents;
using Stellaegis.Blobs;
using Stellaegis.MultiTenant.Context;
using Stellaegis.Primitives;

namespace Stellaegis.Documents;

/// <summary>
/// Default <see cref="IDocumentService"/>: resolves the ambient tenant, hashes content (SHA-256) to
/// deduplicate identical re-uploads, and orchestrates the blob write with the metadata move
/// (blob-first, so a metadata failure leaves a cleanable orphan blob rather than a dangling pointer).
/// </summary>
/// <remarks>
/// When an <see cref="IBackplane"/> is available it publishes <see cref="DocumentIndexedEvent"/> on
/// create/content-change and <see cref="DocumentTrashedEvent"/> on trash, so tenant applications can
/// maintain their own search projection. The dependency is optional: in-memory/dev and unit-test
/// scenarios run without a backplane and simply skip publication.
/// </remarks>
internal sealed class DocumentService(
    IDocumentStore store,
    ITenantContext tenantContext,
    IBlobStore blobStore,
    IOptions<DocumentsOptions> options,
    ILogger<DocumentService> logger,
    IBackplane? backplane = null) : IDocumentService
{
    private readonly IDocumentStore _store = Guard.NotNull(store);
    private readonly ITenantContext _tenantContext = Guard.NotNull(tenantContext);
    private readonly IBlobStore _blobStore = Guard.NotNull(blobStore);
    private readonly string _blobSpace = Guard.NotWhiteSpace(Guard.NotNull(options).Value.BlobSpace);
    private readonly ILogger<DocumentService> _logger = Guard.NotNull(logger);
    private readonly IBackplane? _backplane = backplane;

    public async Task<Document> CreateAsync(string documentType, Stream content, string actor, Guid? businessObjectLink = null, bool isVersioned = false, CancellationToken cancellationToken = default)
    {
        Guard.NotWhiteSpace(documentType);
        Guard.NotNull(content);
        Guard.NotWhiteSpace(actor);
        var tenantSlug = CurrentTenantSlug();

        var bytes = await ReadAllAsync(content, cancellationToken).ConfigureAwait(false);
        var hash = HashHex(bytes);
        var id = Guid.NewGuid();
        var key = isVersioned ? $"{id:N}/v1" : $"{id:N}";

        await WriteBlobAsync(tenantSlug, key, bytes, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var document = new Document
        {
            DocumentUniqueId = id,
            TenantSlug = tenantSlug,
            DocumentType = documentType,
            StoragePath = key,
            ContentHash = hash,
            BusinessObjectLink = businessObjectLink,
            Status = DocumentStatus.Active,
            IsVersioned = isVersioned,
            CreatedAt = now,
            CreatedBy = actor,
        };
        var initialVersion = isVersioned
            ? new DocumentVersion { DocumentUniqueId = id, VersionNumber = 1, StoragePath = key, CreatedAt = now, CreatedBy = actor }
            : null;

        await _store.CreateAsync(document, initialVersion, cancellationToken).ConfigureAwait(false);
        await PublishIndexedAsync(document, cancellationToken).ConfigureAwait(false);
        return document;
    }

    public async Task<Document> UpdateContentAsync(Guid documentUniqueId, Stream content, string actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(content);
        Guard.NotWhiteSpace(actor);
        var tenantSlug = CurrentTenantSlug();

        var current = await _store.GetAsync(documentUniqueId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.TenantSlug != tenantSlug || current.Status != DocumentStatus.Active)
            throw new DocumentNotFoundException(documentUniqueId);

        var bytes = await ReadAllAsync(content, cancellationToken).ConfigureAwait(false);
        var hash = HashHex(bytes);
        if (string.Equals(hash, current.ContentHash, StringComparison.Ordinal))
            return current; // identical content: neither overwrite nor a superfluous version

        var now = DateTimeOffset.UtcNow;
        if (current.IsVersioned)
        {
            var next = await _store.GetMaxVersionNumberAsync(documentUniqueId, cancellationToken).ConfigureAwait(false) + 1;
            var key = $"{documentUniqueId:N}/v{next}";
            await WriteBlobAsync(tenantSlug, key, bytes, cancellationToken).ConfigureAwait(false);

            var updated = current with { StoragePath = key, ContentHash = hash, ModifiedAt = now, ModifiedBy = actor };
            var version = new DocumentVersion { DocumentUniqueId = documentUniqueId, VersionNumber = next, StoragePath = key, CreatedAt = now, CreatedBy = actor };
            await _store.UpdateCurrentAsync(updated, version, cancellationToken).ConfigureAwait(false);
            await PublishIndexedAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }

        // Non-versioned: overwrite in place at the stable key.
        await WriteBlobAsync(tenantSlug, current.StoragePath, bytes, cancellationToken).ConfigureAwait(false);
        var overwritten = current with { ContentHash = hash, ModifiedAt = now, ModifiedBy = actor };
        await _store.UpdateCurrentAsync(overwritten, null, cancellationToken).ConfigureAwait(false);
        await PublishIndexedAsync(overwritten, cancellationToken).ConfigureAwait(false);
        return overwritten;
    }

    public async Task<Stream> OpenContentAsync(Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        var (tenantSlug, document) = await RequireAsync(documentUniqueId, cancellationToken).ConfigureAwait(false);
        return await OpenBlobAsync(documentUniqueId, Location(tenantSlug, document.StoragePath), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Uri?> TryGetContentUrlAsync(Guid documentUniqueId, TimeSpan validity, CancellationToken cancellationToken = default)
    {
        var (tenantSlug, document) = await RequireAsync(documentUniqueId, cancellationToken).ConfigureAwait(false);
        return await _blobStore.TryGetDownloadUrlAsync(
            Location(tenantSlug, document.StoragePath), validity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> OpenVersionContentAsync(Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default)
    {
        var (tenantSlug, _) = await RequireAsync(documentUniqueId, cancellationToken).ConfigureAwait(false);
        var version = await _store.GetVersionAsync(documentUniqueId, versionNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new DocumentNotFoundException(documentUniqueId);
        return await OpenBlobAsync(documentUniqueId, Location(tenantSlug, version.StoragePath), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Document?> GetAsync(Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        var tenantSlug = CurrentTenantSlug();
        var document = await _store.GetAsync(documentUniqueId, cancellationToken).ConfigureAwait(false);
        return document is not null && document.TenantSlug == tenantSlug ? document : null;
    }

    public Task<IReadOnlyList<Document>> ListAsync(CancellationToken cancellationToken = default)
        => _store.ListByTenantAsync(CurrentTenantSlug(), cancellationToken);

    public async Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(documentUniqueId, cancellationToken).ConfigureAwait(false);
        return await _store.GetVersionsAsync(documentUniqueId, cancellationToken).ConfigureAwait(false);
    }

    public async Task TrashAsync(Guid documentUniqueId, string actor, CancellationToken cancellationToken = default)
    {
        Guard.NotWhiteSpace(actor);
        var (tenantSlug, _) = await RequireAsync(documentUniqueId, cancellationToken).ConfigureAwait(false);
        await _store.SetStatusAsync(documentUniqueId, DocumentStatus.Trashed, actor, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        if (_backplane is not null)
            await _backplane.PublishAsync(
                new DocumentTrashedEvent { TenantSlug = tenantSlug, DocumentId = documentUniqueId.ToString("N") },
                cancellationToken).ConfigureAwait(false);
    }

    private Task PublishIndexedAsync(Document document, CancellationToken cancellationToken)
    {
        if (_backplane is null)
            return Task.CompletedTask;

        return _backplane.PublishAsync(
            new DocumentIndexedEvent
            {
                TenantSlug = document.TenantSlug,
                DocumentId = document.DocumentUniqueId.ToString("N"),
                DocumentType = document.DocumentType,
                ContentHash = document.ContentHash,
                BusinessObjectLink = document.BusinessObjectLink?.ToString("N"),
                IsVersioned = document.IsVersioned,
            },
            cancellationToken);
    }

    private async Task<(string TenantSlug, Document Document)> RequireAsync(Guid documentUniqueId, CancellationToken cancellationToken)
    {
        var tenantSlug = CurrentTenantSlug();
        var document = await _store.GetAsync(documentUniqueId, cancellationToken).ConfigureAwait(false);
        if (document is null || document.TenantSlug != tenantSlug)
            throw new DocumentNotFoundException(documentUniqueId);
        return (tenantSlug, document);
    }

    private async Task<Stream> OpenBlobAsync(Guid documentUniqueId, BlobLocation location, CancellationToken cancellationToken)
    {
        var stream = await _blobStore.OpenReadAsync(location, cancellationToken).ConfigureAwait(false);
        if (stream is not null)
            return stream;

        // Metadata without content is a storage inconsistency: the caller gets the same answer as for an
        // absent document, and the log keeps what operations need to repair it.
        _logger.LogError("Document {DocumentUniqueId} is indexed but has no content at {BlobLocation}.", documentUniqueId, location);
        throw new DocumentNotFoundException(documentUniqueId);
    }

    private Task WriteBlobAsync(string tenantSlug, string key, byte[] bytes, CancellationToken cancellationToken)
        => _blobStore.WriteAsync(Location(tenantSlug, key), new MemoryStream(bytes, writable: false), cancellationToken);

    private BlobLocation Location(string holder, string key) => new(holder, _blobSpace, key);

    private string CurrentTenantSlug()
    {
        if (!_tenantContext.TryGetTenantSlug(out var tenantSlug))
            throw new InvalidOperationException("No tenant context available. Document operations require an active tenant.");
        return tenantSlug;
    }

    private static string HashHex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<byte[]> ReadAllAsync(Stream content, CancellationToken cancellationToken)
    {
        if (content is MemoryStream existing)
            return existing.ToArray();

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
