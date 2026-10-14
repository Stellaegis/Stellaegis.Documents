using System.Buffers.Binary;
using System.Buffers.Text;
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
/// <para>
/// Content is stored flat, one blob per content, under a key derived from the document's identity alone:
/// <c>{id:N}-{version}</c>. No key is stored, so nothing about the storage layout reaches the document, the
/// store or the database.
/// </para>
/// <para>
/// Content streams through: it is hashed and counted on its way to the blob store, never held in memory.
/// Each change of content goes to the next number, versioned document or not, and the document's
/// pointer slides to it once the new content is complete and checked: no content is ever overwritten in
/// place, so a reader always gets a whole one. A versioned document keeps its former contents as its
/// history; a document that is not versioned has its former content deleted after the pointer moved.
/// </para>
/// <para>
/// Publishes <see cref="DocumentIndexedEvent"/> on create/content-change and <see cref="DocumentTrashedEvent"/>
/// on trash through <see cref="IEventPublisher"/>, after the metadata is stored, so consumers can maintain
/// their own search projection. Without a host transport the publisher is <see cref="NullEventPublisher"/>.
/// </para>
/// </remarks>
internal sealed class DocumentService(
    IDocumentStore store,
    IBlobStore blobStore,
    IOptions<DocumentsOptions> options,
    IEventPublisher events,
    ILogger<DocumentService> logger) : IDocumentService
{
    private const int PurgeBatchSize = 100;

    private readonly IDocumentStore _store = Guard.NotNull(store);
    private readonly IBlobStore _blobStore = Guard.NotNull(blobStore);
    private readonly string _blobName = Guard.NotWhiteSpace(Guard.NotNull(options).Value.BlobName);
    private readonly ILogger<DocumentService> _logger = Guard.NotNull(logger);
    private readonly int _listPageSize = Guard.Satisfies(Guard.NotNull(options).Value.ListPageSize, size => size is >= 1 and <= 1000,
        $"{nameof(DocumentsOptions)}.{nameof(DocumentsOptions.ListPageSize)} must be between 1 and 1000.");
    private readonly IEventPublisher _events = Guard.NotNull(events);

    public async Task<Document> CreateAsync(Space space, string documentType, DocumentUpload upload, Actor actor, bool isVersioned = false, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);
        Guard.NotWhiteSpace(documentType);
        CheckUpload(upload);
        Guard.NotNull(actor);

        var id = Guid.NewGuid();
        var (hash, length) = await WriteCheckedAsync(space, id, 1, upload, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var document = new Document
        {
            DocumentUniqueId = id,
            Space = space,
            DocumentType = documentType,
            VersionNumber = 1,
            ContentHash = hash,
            FileName = upload.FileName,
            ContentType = upload.ContentType,
            ContentLength = length,
            Status = DocumentStatus.Active,
            IsVersioned = isVersioned,
            CreatedAt = now,
            CreatedBy = actor,
        };
        var initialVersion = isVersioned
            ? NewVersion(id, 1, upload, length, hash, now, actor)
            : null;

        await _store.CreateAsync(document, initialVersion, cancellationToken).ConfigureAwait(false);
        await PublishIndexedAsync(document, now, cancellationToken).ConfigureAwait(false);
        return document;
    }

    public async Task<Document> UpdateContentAsync(Space space, Guid documentUniqueId, DocumentUpload upload, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);
        CheckUpload(upload);
        Guard.NotNull(actor);

        var current = await _store.GetAsync(space, documentUniqueId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Status != DocumentStatus.Active)
            throw new DocumentNotFoundException(documentUniqueId);

        // The sender's digest equals the current one: the content is the same, there is nothing to read.
        if (string.Equals(upload.ExpectedContentHash, current.ContentHash, StringComparison.OrdinalIgnoreCase))
            return current;

        // Known limit: two concurrent updates compute the same number. The store refuses the second
        // revision, but its content has already replaced the first one's under the shared key.
        var next = current.VersionNumber + 1;
        var (hash, length) = await WriteCheckedAsync(space, documentUniqueId, next, upload, cancellationToken).ConfigureAwait(false);
        if (string.Equals(hash, current.ContentHash, StringComparison.Ordinal))
        {
            // Identical content: neither a change nor a superfluous version.
            await DeleteBlobAsync(space, documentUniqueId, next, cancellationToken).ConfigureAwait(false);
            return current;
        }

        var now = DateTimeOffset.UtcNow;
        var updated = current with { VersionNumber = next, ContentHash = hash, FileName = upload.FileName, ContentType = upload.ContentType, ContentLength = length, ModifiedAt = now, ModifiedBy = actor };
        var version = current.IsVersioned ? NewVersion(documentUniqueId, next, upload, length, hash, now, actor) : null;
        try
        {
            await _store.UpdateCurrentAsync(updated, version, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeleteBlobAsync(space, documentUniqueId, next, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // Without history, the former content goes once nothing points to it any more.
        if (!current.IsVersioned)
            await DeleteBlobAsync(space, documentUniqueId, current.VersionNumber, CancellationToken.None).ConfigureAwait(false);

        await PublishIndexedAsync(updated, now, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<Stream> OpenContentAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        var document = await RequireAsync(space, documentUniqueId, cancellationToken).ConfigureAwait(false);
        return await OpenBlobAsync(documentUniqueId, Location(space, documentUniqueId, document.VersionNumber), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> OpenVersionContentAsync(Space space, Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);

        _ = await _store.GetVersionAsync(space, documentUniqueId, versionNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new DocumentNotFoundException(documentUniqueId);
        return await OpenBlobAsync(documentUniqueId, Location(space, documentUniqueId, versionNumber), cancellationToken).ConfigureAwait(false);
    }

    public Task<Document?> GetAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken = default)
        => _store.GetAsync(Guard.NotNull(space), documentUniqueId, cancellationToken);

    public async Task<DocumentPage> ListAsync(Space space, string? continuationToken = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);
        var after = continuationToken is null ? null : DecodeCursor(continuationToken);

        // One more than a page tells whether a next page exists without a second query.
        var documents = await _store.ListAsync(space, after, _listPageSize + 1, cancellationToken).ConfigureAwait(false);
        if (documents.Count <= _listPageSize)
            return new DocumentPage(documents, null);

        var page = documents.Take(_listPageSize).ToList();
        var last = page[^1];
        return new DocumentPage(page, EncodeCursor(new DocumentCursor(last.CreatedAt, last.DocumentUniqueId)));
    }

    public async Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(space, documentUniqueId, cancellationToken).ConfigureAwait(false);
        return await _store.GetVersionsAsync(space, documentUniqueId, cancellationToken).ConfigureAwait(false);
    }

    public async Task TrashAsync(Space space, Guid documentUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(actor);
        await RequireAsync(space, documentUniqueId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await _store.SetStatusAsync(space, documentUniqueId, DocumentStatus.Trashed, actor, now, cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(
            new DocumentTrashedEvent { OccurredAt = now, Compartment = space.Compartment, IsShared = space.IsShared, DocumentUniqueId = documentUniqueId },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PurgeCompartmentAsync(Compartment compartment, CancellationToken cancellationToken = default)
    {
        Space space = Guard.NotNull(compartment);

        // Batches keep memory bounded. Deleted documents leave the next query, so no position is kept:
        // the purge takes what remains until nothing does.
        var purged = new HashSet<Guid>();
        IReadOnlyList<Document> batch;
        do
        {
            batch = await _store.ListAnyStatusAsync(space, PurgeBatchSize, cancellationToken).ConfigureAwait(false);
            foreach (var document in batch)
            {
                if (!purged.Add(document.DocumentUniqueId))
                    throw new InvalidOperationException($"Document '{document.DocumentUniqueId:N}' is still listed after its deletion: the store did not delete it.");

                // Content first: a purge cut between the two leaves metadata that the next call finds and
                // completes, never content that nothing points to any more.
                for (var version = 1; version <= document.VersionNumber; version++)
                    await _blobStore.DeleteAsync(Location(space, document.DocumentUniqueId, version), cancellationToken).ConfigureAwait(false);

                await _store.DeleteAsync(space, document.DocumentUniqueId, cancellationToken).ConfigureAwait(false);
            }
        }
        while (batch.Count == PurgeBatchSize);

        _logger.LogInformation("Purged {DocumentCount} documents of compartment {Compartment}.", purged.Count, compartment);
        return purged.Count;
    }

    private Task PublishIndexedAsync(Document document, DateTimeOffset occurredAt, CancellationToken cancellationToken)
        => _events.PublishAsync(
            new DocumentIndexedEvent
            {
                OccurredAt = occurredAt,
                Compartment = document.Space.Compartment,
                IsShared = document.Space.IsShared,
                DocumentUniqueId = document.DocumentUniqueId,
                DocumentType = document.DocumentType,
                ContentHash = document.ContentHash,
                IsVersioned = document.IsVersioned,
            },
            cancellationToken);

    private async Task<Document> RequireAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken)
    {
        Guard.NotNull(space);
        return await _store.GetAsync(space, documentUniqueId, cancellationToken).ConfigureAwait(false)
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

    // Streams the content to its blob while hashing it, then checks it against the sender's digest: content
    // that does not match is deleted before anything points to it.
    private async Task<(string Hash, long Length)> WriteCheckedAsync(
        Space space, Guid documentUniqueId, int versionNumber, DocumentUpload upload, CancellationToken cancellationToken)
    {
        using var hashing = new HashingStream(upload.Content);
        await _blobStore.WriteAsync(Location(space, documentUniqueId, versionNumber), hashing, cancellationToken).ConfigureAwait(false);

        var hash = hashing.ContentHash;
        if (upload.ExpectedContentHash is { } expected && !string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
        {
            await DeleteBlobAsync(space, documentUniqueId, versionNumber, CancellationToken.None).ConfigureAwait(false);
            throw new UserFacingException("The content does not match its digest: it was damaged on its way.");
        }

        return (hash, hashing.ContentLength);
    }

    private Task DeleteBlobAsync(Space space, Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken)
        => _blobStore.DeleteAsync(Location(space, documentUniqueId, versionNumber), cancellationToken);

    private BlobLocation Location(Space space, Guid documentUniqueId, int versionNumber)
    {
        var key = $"{documentUniqueId:N}-{versionNumber}";
        return space.IsShared ? BlobLocation.Shared(_blobName, key) : new BlobLocation(space.Compartment, _blobName, key);
    }

    // A token only positions a list the store already scopes to the space: altering it moves the page,
    // it never reaches another space's documents.
    private static string EncodeCursor(DocumentCursor cursor)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0, 8), cursor.CreatedAt.UtcTicks);
        cursor.DocumentUniqueId.TryWriteBytes(bytes.AsSpan(8, 16));
        return Base64Url.EncodeToString(bytes);
    }

    private static DocumentCursor DecodeCursor(string continuationToken)
    {
        Span<byte> bytes = stackalloc byte[24];
        if (!TryDecode(continuationToken, bytes))
            throw new UserFacingException("The continuation token is not valid.");

        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes[..8]);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            throw new UserFacingException("The continuation token is not valid.");

        return new DocumentCursor(new DateTimeOffset(ticks, TimeSpan.Zero), new Guid(bytes[8..]));
    }

    // TryDecodeFromChars returns false only when the destination is too small: malformed input throws.
    private static bool TryDecode(string token, Span<byte> bytes)
    {
        try
        {
            return Base64Url.TryDecodeFromChars(token, bytes, out var written) && written == bytes.Length;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void CheckUpload(DocumentUpload upload)
    {
        Guard.NotNull(upload);
        Guard.NotNull(upload.Content);
        Guard.Satisfies(Guard.NotWhiteSpace(upload.FileName), name => name.Length <= DocumentUpload.MaxFileNameLength,
            $"A file name has at most {DocumentUpload.MaxFileNameLength} characters.");
        Guard.Satisfies(Guard.NotWhiteSpace(upload.ContentType), type => type.Length <= DocumentUpload.MaxContentTypeLength,
            $"A media type has at most {DocumentUpload.MaxContentTypeLength} characters.");
    }

    private static DocumentVersion NewVersion(Guid documentUniqueId, int versionNumber, DocumentUpload upload, long contentLength, string contentHash, DateTimeOffset now, Actor actor)
        => new()
        {
            DocumentUniqueId = documentUniqueId,
            VersionNumber = versionNumber,
            FileName = upload.FileName,
            ContentType = upload.ContentType,
            ContentLength = contentLength,
            ContentHash = contentHash,
            CreatedAt = now,
            CreatedBy = actor,
        };

}
