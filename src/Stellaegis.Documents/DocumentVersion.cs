namespace Stellaegis.Documents;

/// <summary>
/// A stored revision of a versioned document. Double key (<see cref="DocumentUniqueId"/> +
/// <see cref="VersionNumber"/>): the document keeps its stable public identity, only the revision
/// changes. Carries its own file metadata and audit — no content hash (change detection compares the
/// incoming content against the current pointer only) and no free-text note (revision semantics
/// belong to the consumer's own store, not to this module).
/// </summary>
public sealed record DocumentVersion
{
    /// <summary>The document this revision belongs to.</summary>
    public Guid DocumentUniqueId { get; init; }

    /// <summary>Revision number (1-based, increasing).</summary>
    public int VersionNumber { get; init; }

    /// <summary>The file name the uploader gave the revision's content. Free text, never part of a storage key.</summary>
    public required string FileName { get; init; }

    /// <summary>The media type of the revision's content, as declared by the uploader.</summary>
    public required string ContentType { get; init; }

    /// <summary>The length of the revision's content, in bytes.</summary>
    public long ContentLength { get; init; }

    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Creator subject.</summary>
    public required string CreatedBy { get; init; }
}
