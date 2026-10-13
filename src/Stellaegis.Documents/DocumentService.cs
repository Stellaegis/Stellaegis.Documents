using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Blobs;
using Stellaegis.Primitives;

namespace Stellaegis.Documents;

/// <summary>
/// Default <see cref="IDocumentService"/>: hashes content (SHA-256) to deduplicate identical re-uploads,
/// and orchestrates the blob write with the metadata move (blob-first, so a metadata failure leaves a
/// cleanable orphan blob rather than a dangling pointer).
/// </summary>
/// <remarks>
/// Publishes <see cref="DocumentIndexedEvent"/> on create/content-change and <see cref="DocumentTrashedEvent"/>
/// on trash through <see cref="IEventPublisher"/>, after the metadata is stored, so consumers can maintain
/// their own search projection. Without a host transport the publisher is <see cref="NullEventPublisher"/>.
/// </remarks>
internal sealed class DocumentService(
    IDocumentStore store,
    IBlobStore blobStore,
    IOptions<DocumentsOptions> options,
    IEventPublisher events,
    ILogger<DocumentService> logger) : IDocumentService
{
    private readonly IDocumentStore _store = Guard.NotNull(store);
    private readonly IBlobStore _blobStore = Guard.NotNull(blobStore);
    private readonly string _blobSpace = Guard.NotWhiteSpace(Guard.NotNull(options).Value.BlobSpace);
    private readonly ILogger<DocumentService> _logger = Guard.NotNull(logger);
    private readonly IEventPublisher _events = Guard.NotNull(events);

    public async Task<Document> CreateAsync(string holder, string documentType, Stream content, string actor, Guid? businessObjectLink = null, bool isVersioned = false, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(holder);
        Guard.NotWhiteSpace(documentType);
        Guard.NotNull(content);
        Guard.NotWhiteSpace(actor);

        var bytes = await ReadAllAsync(content, cancellationToken).ConfigureAwait(false);
        var hash = HashHex(bytes);
        var id = Guid.NewGuid();
        var key = isVersioned ? $"{id:N}/v1" : $"{id:N}";

        await WriteBlobAsync(holder, key, bytes, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var document = new Document
        {
            DocumentUniqueId = id,
            Holder = holder,
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
        await PublishIndexedAsync(document, now, cancellationToken).ConfigureAwait(false);
        return document;
    }

    public async Task<Document> UpdateContentAsync(string holder, Guid documentUniqueId, Stream content, string actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(holder);
        Guard.NotNull(content);
        Guard.NotWhiteSpace(actor);

        var current = await _store.GetAsync(holder, documentUniqueId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Status != DocumentStatus.Active)
            throw new DocumentNotFoundException(documentUniqueId);

        var bytes = await ReadAllAsync(content, cancellationToken).ConfigureAwait(false);
        var hash = HashHex(bytes);
        if (string.Equals(hash, current.ContentHash, StringComparison.Ordinal))
            return current; // identical content: neither overwrite nor a superfluous version

        var now = DateTimeOffset.UtcNow;
        if (current.IsVersioned)
        {
            var next = await _store.GetMaxVersionNumberAsync(holder, documentUniqueId, cancellationToken).ConfigureAwait(false) + 1;
            var key = $"{documentUniqueId:N}/v{next}";
            await WriteBlobAsync(holder, key, bytes, cancellationToken).ConfigureAwait(false);

            var updated = current with { StoragePath = key, ContentHash = hash, ModifiedAt = now, ModifiedBy = actor };
            var version = new DocumentVersion { DocumentUniqueId = documentUniqueId, VersionNumber = next, StoragePath = key, CreatedAt = now, CreatedBy = actor };
            await _store.UpdateCurrentAsync(updated, version, cancellationToken).ConfigureAwait(false);
            await PublishIndexedAsync(updated, now, cancellationToken).ConfigureAwait(false);
            return updated;
        }

        // Non-versioned: overwrite in place at the stable key.
        await WriteBlobAsync(holder, current.StoragePath, bytes, cancellationToken).ConfigureAwait(false);
        var overwritten = current with { ContentHash = hash, ModifiedAt = now, ModifiedBy = actor };
        await _store.UpdateCurrentAsync(overwritten, null, cancellationToken).ConfigureAwait(false);
        await PublishIndexedAsync(overwritten, now, cancellationToken).ConfigureAwait(false);
        return overwritten;
    }

    public async Task<Stream> OpenContentAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        var document = await RequireAsync(holder, documentUniqueId, cancellationToken).ConfigureAwait(false);
        return await OpenBlobAsync(documentUniqueId, Location(holder, document.StoragePath), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Uri?> TryGetContentUrlAsync(string holder, Guid documentUniqueId, TimeSpan validity, CancellationToken cancellationToken = default)
    {
        var document = await RequireAsync(holder, documentUniqueId, cancellationToken).ConfigureAwait(false);
        return await _blobStore.TryGetDownloadUrlAsync(
            Location(holder, document.StoragePath), validity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> OpenVersionContentAsync(string holder, Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(holder);

        var version = await _store.GetVersionAsync(holder, documentUniqueId, versionNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new DocumentNotFoundException(documentUniqueId);
        return await OpenBlobAsync(documentUniqueId, Location(holder, version.StoragePath), cancellationToken).ConfigureAwait(false);
    }

    public Task<Document?> GetAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default)
        => _store.GetAsync(Guard.NotNull(holder), documentUniqueId, cancellationToken);

    public Task<IReadOnlyList<Document>> ListAsync(string holder, CancellationToken cancellationToken = default)
        => _store.ListAsync(Guard.NotNull(holder), cancellationToken);

    public async Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(holder, documentUniqueId, cancellationToken).ConfigureAwait(false);
        return await _store.GetVersionsAsync(holder, documentUniqueId, cancellationToken).ConfigureAwait(false);
    }

    public async Task TrashAsync(string holder, Guid documentUniqueId, string actor, CancellationToken cancellationToken = default)
    {
        Guard.NotWhiteSpace(actor);
        await RequireAsync(holder, documentUniqueId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await _store.SetStatusAsync(holder, documentUniqueId, DocumentStatus.Trashed, actor, now, cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(
            new DocumentTrashedEvent { OccurredAt = now, Holder = holder, DocumentUniqueId = documentUniqueId },
            cancellationToken).ConfigureAwait(false);
    }

    private Task PublishIndexedAsync(Document document, DateTimeOffset occurredAt, CancellationToken cancellationToken)
        => _events.PublishAsync(
            new DocumentIndexedEvent
            {
                OccurredAt = occurredAt,
                Holder = document.Holder,
                DocumentUniqueId = document.DocumentUniqueId,
                DocumentType = document.DocumentType,
                ContentHash = document.ContentHash,
                BusinessObjectLink = document.BusinessObjectLink,
                IsVersioned = document.IsVersioned,
            },
            cancellationToken);

    private async Task<Document> RequireAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken)
    {
        Guard.NotNull(holder);
        return await _store.GetAsync(holder, documentUniqueId, cancellationToken).ConfigureAwait(false)
            ?? throw new DocumentNotFoundException(documentUniqueId);
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

    private Task WriteBlobAsync(string holder, string key, byte[] bytes, CancellationToken cancellationToken)
        => _blobStore.WriteAsync(Location(holder, key), new MemoryStream(bytes, writable: false), cancellationToken);

    private BlobLocation Location(string holder, string key) => new(holder, _blobSpace, key);

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
