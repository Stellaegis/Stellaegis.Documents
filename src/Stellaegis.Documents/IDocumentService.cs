using Stellaegis.Primitives;

namespace Stellaegis.Documents;

/// <summary>
/// Document operations in one space: store/replace content (with SHA-256 dedup and opt-in
/// versioning), read current or historical content, list, trash. Content flows through
/// <c>IBlobStore</c>.
/// </summary>
/// <remarks>
/// Every operation names its <see cref="Space"/> — a <see cref="Compartment"/> the documents belong to, or
/// <see cref="Space.Shared"/> for the documents that belong to no compartment — and only ever sees the
/// documents of that space. The space must come from a trusted source (the host's resolution of the
/// caller), never from user input. The actor of a change is recorded on the document.
/// </remarks>
public interface IDocumentService
{
    /// <summary>
    /// Stores a new document's content and indexes it. When <paramref name="isVersioned"/> is true,
    /// records it as version 1.
    /// </summary>
    Task<Document> CreateAsync(Space space, string documentType, DocumentUpload upload, Actor actor, bool isVersioned = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a document's content. Identical content (same hash) is a no-op, whatever its file name
    /// and media type; otherwise the document moves to its next content number, file metadata included,
    /// and a versioned document keeps the former content as a revision.
    /// </summary>
    /// <exception cref="DocumentNotFoundException">No active document with that id for the space.</exception>
    Task<Document> UpdateContentAsync(Space space, Guid documentUniqueId, DocumentUpload upload, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Opens the current content of a document.</summary>
    /// <exception cref="DocumentNotFoundException">No document with that id for the space.</exception>
    Task<Stream> OpenContentAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Opens the content of a specific version.</summary>
    /// <exception cref="DocumentNotFoundException">No such document or version for the space.</exception>
    Task<Stream> OpenVersionContentAsync(Space space, Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default);

    /// <summary>Gets a document's metadata, or <see langword="null"/> when the space has no such document.</summary>
    Task<Document?> GetAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists one page of the space's active documents, most recent first. Pass the previous page's
    /// <see cref="DocumentPage.ContinuationToken"/> to get the next one.
    /// </summary>
    /// <exception cref="UserFacingException"><paramref name="continuationToken"/> is not a token this service issued.</exception>
    Task<DocumentPage> ListAsync(Space space, string? continuationToken = null, CancellationToken cancellationToken = default);

    /// <summary>Lists a document's version history.</summary>
    /// <exception cref="DocumentNotFoundException">No document with that id for the space.</exception>
    Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken = default);

    /// <summary>Marks a document as trashed.</summary>
    /// <exception cref="DocumentNotFoundException">No document with that id for the space.</exception>
    Task TrashAsync(Space space, Guid documentUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every document of <paramref name="compartment"/> for good, whatever its status: content of
    /// every version, then metadata. Irreversible. The shared space has no such purge.
    /// </summary>
    /// <remarks>
    /// Confirmations and any retention delay belong to whoever manages the compartment's life, which calls
    /// this once they are through. The call can be replayed: an interrupted purge leaves the metadata of the
    /// documents it did not finish, and the next call completes them.
    /// </remarks>
    /// <returns>The number of documents deleted.</returns>
    Task<int> PurgeCompartmentAsync(Compartment compartment, CancellationToken cancellationToken = default);
}
