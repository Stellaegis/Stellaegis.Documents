namespace Stellaegis.Documents;

/// <summary>
/// Persistence contract for <see cref="Document"/> (<c>doc.documents</c>) and its revisions
/// (<c>doc.document_versions</c>).
/// </summary>
public interface IDocumentStore
{
    /// <summary>Gets a document by id (any status), or <see langword="null"/>.</summary>
    Task<Document?> GetAsync(Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Lists a tenant's active documents.</summary>
    Task<IReadOnlyList<Document>> ListByTenantAsync(string tenantSlug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a document and, when supplied, its first version — atomically (one transaction in the
    /// persistent store).
    /// </summary>
    Task CreateAsync(Document document, DocumentVersion? initialVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a document's current pointer (<c>storage_path</c>/<c>content_hash</c>) and, when
    /// <paramref name="newVersion"/> is supplied, appends that revision — atomically (the version row
    /// and the pointer move together).
    /// </summary>
    Task UpdateCurrentAsync(Document document, DocumentVersion? newVersion, CancellationToken cancellationToken = default);

    /// <summary>Sets a document's lifecycle status (e.g. trashing).</summary>
    Task SetStatusAsync(Guid documentUniqueId, DocumentStatus status, string modifiedBy, DateTimeOffset modifiedAt, CancellationToken cancellationToken = default);

    /// <summary>Returns the highest version number for a document, or 0 if it has none.</summary>
    Task<int> GetMaxVersionNumberAsync(Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Lists a document's versions, ordered by version number.</summary>
    Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Gets a specific version, or <see langword="null"/>.</summary>
    Task<DocumentVersion?> GetVersionAsync(Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default);
}
