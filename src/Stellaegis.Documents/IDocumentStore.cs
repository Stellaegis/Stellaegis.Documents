namespace Stellaegis.Documents;

/// <summary>
/// Persistence contract for <see cref="Document"/> (<c>doc.documents</c>) and its revisions
/// (<c>doc.document_versions</c>).
/// </summary>
/// <remarks>
/// Every read and every change is scoped to a holder: a document of another holder behaves exactly as an
/// absent one. The scoping lives in the store's own queries, so no caller can forget it.
/// </remarks>
public interface IDocumentStore
{
    /// <summary>Gets a holder's document by id (any status), or <see langword="null"/>.</summary>
    Task<Document?> GetAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Lists a holder's active documents.</summary>
    Task<IReadOnlyList<Document>> ListAsync(string holder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a document and, when supplied, its first version — atomically (one transaction in the
    /// persistent store). The document carries its holder.
    /// </summary>
    Task CreateAsync(Document document, DocumentVersion? initialVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a document's current pointer (<c>storage_path</c>/<c>content_hash</c>) and, when
    /// <paramref name="newVersion"/> is supplied, appends that revision — atomically (the version row
    /// and the pointer move together). Only the document of <paramref name="document"/>'s holder is moved.
    /// </summary>
    Task UpdateCurrentAsync(Document document, DocumentVersion? newVersion, CancellationToken cancellationToken = default);

    /// <summary>Sets the lifecycle status (e.g. trashing) of a holder's document.</summary>
    Task SetStatusAsync(string holder, Guid documentUniqueId, DocumentStatus status, string modifiedBy, DateTimeOffset modifiedAt, CancellationToken cancellationToken = default);

    /// <summary>Returns the highest version number of a holder's document, or 0 if it has none.</summary>
    Task<int> GetMaxVersionNumberAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Lists the versions of a holder's document, ordered by version number.</summary>
    Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Gets a specific version of a holder's document, or <see langword="null"/>.</summary>
    Task<DocumentVersion?> GetVersionAsync(string holder, Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default);
}
