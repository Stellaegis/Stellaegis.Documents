namespace Stellaegis.Documents;

/// <summary>
/// Document operations for one holder: store/replace content (with SHA-256 dedup and opt-in
/// versioning), read current or historical content, list, trash. Content flows through
/// <c>IBlobStore</c>.
/// </summary>
/// <remarks>
/// Every operation names its <c>holder</c> — the party the documents belong to, or the empty string for
/// the platform — and only ever sees that holder's documents. The holder must come from a trusted source
/// (the host's resolution of the caller), never from user input.
/// </remarks>
public interface IDocumentService
{
    /// <summary>
    /// Stores a new document's content and indexes it. When <paramref name="isVersioned"/> is true,
    /// records it as version 1.
    /// </summary>
    Task<Document> CreateAsync(string holder, string documentType, DocumentUpload upload, string actor, Guid? businessObjectLink = null, bool isVersioned = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a document's content. Identical content (same hash) is a no-op, whatever its file name
    /// and media type; otherwise a versioned document gains a new revision while a non-versioned one is
    /// overwritten in place, file metadata included.
    /// </summary>
    /// <exception cref="DocumentNotFoundException">No active document with that id for the holder.</exception>
    Task<Document> UpdateContentAsync(string holder, Guid documentUniqueId, DocumentUpload upload, string actor, CancellationToken cancellationToken = default);

    /// <summary>Opens the current content of a document.</summary>
    /// <exception cref="DocumentNotFoundException">No document with that id for the holder.</exception>
    Task<Stream> OpenContentAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tries to obtain a short-lived direct download URL for a document's current content, letting the
    /// browser fetch bytes straight from the storage backend. Returns <see langword="null"/> when the
    /// backend cannot pre-sign (e.g. the filesystem adapter) — the caller must then stream via
    /// <see cref="OpenContentAsync"/>.
    /// </summary>
    /// <exception cref="DocumentNotFoundException">No document with that id for the holder.</exception>
    Task<Uri?> TryGetContentUrlAsync(string holder, Guid documentUniqueId, TimeSpan validity, CancellationToken cancellationToken = default);

    /// <summary>Opens the content of a specific version.</summary>
    /// <exception cref="DocumentNotFoundException">No such document or version for the holder.</exception>
    Task<Stream> OpenVersionContentAsync(string holder, Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default);

    /// <summary>Gets a document's metadata, or <see langword="null"/> when the holder has no such document.</summary>
    Task<Document?> GetAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Lists the holder's active documents.</summary>
    Task<IReadOnlyList<Document>> ListAsync(string holder, CancellationToken cancellationToken = default);

    /// <summary>Lists a document's version history.</summary>
    /// <exception cref="DocumentNotFoundException">No document with that id for the holder.</exception>
    Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Marks a document as trashed.</summary>
    /// <exception cref="DocumentNotFoundException">No document with that id for the holder.</exception>
    Task TrashAsync(string holder, Guid documentUniqueId, string actor, CancellationToken cancellationToken = default);
}
