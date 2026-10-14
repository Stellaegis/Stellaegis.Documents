using Stellaegis.Primitives;

namespace Stellaegis.Documents;

/// <summary>
/// Persistence contract for <see cref="Document"/> (<c>doc.documents</c>) and its revisions
/// (<c>doc.document_versions</c>).
/// </summary>
/// <remarks>
/// Every read and every change is scoped to a space: a document of another space behaves exactly as an
/// absent one. The scoping lives in the store's own queries, so no caller can forget it.
/// </remarks>
public interface IDocumentStore
{
    /// <summary>Gets a space's document by id (any status), or <see langword="null"/>.</summary>
    Task<Document?> GetAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists at most <paramref name="limit"/> of a space's active documents, most recent first
    /// (<see cref="DocumentCursor"/> order), starting after <paramref name="after"/> when given.
    /// </summary>
    Task<IReadOnlyList<Document>> ListAsync(Space space, DocumentCursor? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>Lists at most <paramref name="limit"/> of a space's documents, whatever their status, in no set order.</summary>
    Task<IReadOnlyList<Document>> ListAnyStatusAsync(Space space, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a document and, when supplied, its first version — atomically (one transaction in the
    /// persistent store). The document carries its space.
    /// </summary>
    Task CreateAsync(Document document, DocumentVersion? initialVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a document to its new current content (version number, hash, file metadata) and, when
    /// <paramref name="newVersion"/> is supplied, appends that revision — atomically (the version row
    /// and the pointer move together). Only the document of <paramref name="document"/>'s space is moved.
    /// </summary>
    Task UpdateCurrentAsync(Document document, DocumentVersion? newVersion, CancellationToken cancellationToken = default);

    /// <summary>Sets the lifecycle status (e.g. trashing) of a space's document.</summary>
    Task SetStatusAsync(Space space, Guid documentUniqueId, DocumentStatus status, Actor modifiedBy, DateTimeOffset modifiedAt, CancellationToken cancellationToken = default);

    /// <summary>Lists the versions of a space's document, ordered by version number.</summary>
    Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Gets a specific version of a space's document, or <see langword="null"/>.</summary>
    Task<DocumentVersion?> GetVersionAsync(Space space, Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a space's document and its versions for good — atomically. No-op when the space has no
    /// such document.
    /// </summary>
    Task DeleteAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken = default);
}
