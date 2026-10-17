using Stellaegis.Primitives;

namespace Stellaegis.Documents;

/// <summary>
/// Persistence contract for <see cref="Document"/> (<c>doc.documents</c>) and its revisions
/// (<c>doc.document_versions</c>).
/// </summary>
/// <remarks>
/// <para>Every read and every change is scoped to a space: a document of another space behaves exactly as an
/// absent one. The scoping lives in the store's own queries, so no caller can forget it.</para>
/// <para>Every call also names its actor, who reads or changes. A store over a database hands the space and the
/// actor to it — the session context of <c>ISpaceUnitOfWorkFactory</c> — so that the database can isolate the
/// rows too; a store without one ignores the actor of a read.</para>
/// </remarks>
public interface IDocumentStore
{
    /// <summary>Gets a space's document by id (any status), or <see langword="null"/>.</summary>
    Task<Document?> GetAsync(Space space, Guid documentUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists at most <paramref name="limit"/> of a space's active documents, most recent first
    /// (<see cref="DocumentCursor"/> order), starting after <paramref name="after"/> when given.
    /// </summary>
    Task<IReadOnlyList<Document>> ListAsync(Space space, DocumentCursor? after, int limit, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Lists at most <paramref name="limit"/> of a space's documents, whatever their status, in no set order.</summary>
    Task<IReadOnlyList<Document>> ListAnyStatusAsync(Space space, int limit, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a document and, when supplied, its first version — atomically (one transaction in the
    /// persistent store). The document carries its space.
    /// </summary>
    Task CreateAsync(Document document, DocumentVersion? initialVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a document to its new current content (version number, hash, file metadata) and, when
    /// <paramref name="newVersion"/> is supplied, appends that revision — atomically (the version row
    /// and the pointer move together). Only the document of <paramref name="document"/>'s space is moved.
    /// </summary>
    Task UpdateCurrentAsync(Document document, DocumentVersion? newVersion, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Sets the lifecycle status (e.g. trashing) of a space's document. <paramref name="modifiedBy"/> is the actor.</summary>
    Task SetStatusAsync(Space space, Guid documentUniqueId, DocumentStatus status, Actor modifiedBy, DateTimeOffset modifiedAt, CancellationToken cancellationToken = default);

    /// <summary>Lists the versions of a space's document, ordered by version number.</summary>
    Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(Space space, Guid documentUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Gets a specific version of a space's document, or <see langword="null"/>.</summary>
    Task<DocumentVersion?> GetVersionAsync(Space space, Guid documentUniqueId, int versionNumber, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a space's document and its versions for good — atomically. No-op when the space has no
    /// such document.
    /// </summary>
    Task DeleteAsync(Space space, Guid documentUniqueId, Actor actor, CancellationToken cancellationToken = default);
}
