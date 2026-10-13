namespace Stellaegis.Documents;

/// <summary>
/// Tenant-facing document operations: store/replace content (with SHA-256 dedup and opt-in
/// versioning), read current or historical content, list, trash. Content flows through
/// <c>IBlobStore</c>; the tenant is resolved from the ambient <c>ITenantContext</c>.
/// </summary>
public interface IDocumentService
{
    /// <summary>
    /// Stores a new document's content and indexes it. When <paramref name="isVersioned"/> is true,
    /// records it as version 1.
    /// </summary>
    Task<Document> CreateAsync(string documentType, Stream content, string actor, Guid? businessObjectLink = null, bool isVersioned = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a document's content. Identical content (same hash) is a no-op; otherwise a versioned
    /// document gains a new revision while a non-versioned one is overwritten in place.
    /// </summary>
    /// <exception cref="DocumentNotFoundException">No active document with that id for the current tenant.</exception>
    Task<Document> UpdateContentAsync(Guid documentUniqueId, Stream content, string actor, CancellationToken cancellationToken = default);

    /// <summary>Opens the current content of a document.</summary>
    /// <exception cref="DocumentNotFoundException">No document with that id for the current tenant.</exception>
    Task<Stream> OpenContentAsync(Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tries to obtain a short-lived direct download URL for a document's current content, letting the
    /// browser fetch bytes straight from the storage backend. Returns <see langword="null"/> when the
    /// backend cannot pre-sign (e.g. the filesystem adapter) — the caller must then stream via
    /// <see cref="OpenContentAsync"/>.
    /// </summary>
    /// <exception cref="DocumentNotFoundException">No document with that id for the current tenant.</exception>
    Task<Uri?> TryGetContentUrlAsync(Guid documentUniqueId, TimeSpan validity, CancellationToken cancellationToken = default);

    /// <summary>Opens the content of a specific version.</summary>
    /// <exception cref="DocumentNotFoundException">No such document or version for the current tenant.</exception>
    Task<Stream> OpenVersionContentAsync(Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default);

    /// <summary>Gets a document's metadata (current tenant only), or <see langword="null"/>.</summary>
    Task<Document?> GetAsync(Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Lists the current tenant's active documents.</summary>
    Task<IReadOnlyList<Document>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists a document's version history.</summary>
    Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Marks a document as trashed.</summary>
    Task TrashAsync(Guid documentUniqueId, string actor, CancellationToken cancellationToken = default);
}
