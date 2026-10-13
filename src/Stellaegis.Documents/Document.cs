namespace Stellaegis.Documents;

/// <summary>
/// Index entry: the existence and identity of a document — never its content (which lives in the
/// blob store) nor its business semantics. <see cref="VersionNumber"/>, <see cref="ContentHash"/> and the
/// file metadata always describe the current content (one GUID, one read, the document); the history of
/// a versioned document lives in <see cref="DocumentVersion"/>.
/// </summary>
public sealed record Document
{
    /// <summary>Stable public GUID — published in events and referenced by the consumer's projection.</summary>
    public Guid DocumentUniqueId { get; init; }

    /// <summary>The holder the document belongs to, or the empty string for the platform.</summary>
    public required string Holder { get; init; }

    /// <summary>Application-defined document type code.</summary>
    public required string DocumentType { get; init; }

    /// <summary>
    /// The number of the current content, from 1, increased by each change of content. A versioned
    /// document keeps its former contents as versions; one that is not versioned keeps only the current.
    /// </summary>
    public int VersionNumber { get; init; } = 1;

    /// <summary>SHA-256 (hex, lowercase) of the current content.</summary>
    public required string ContentHash { get; init; }

    /// <summary>The file name the uploader gave the current content. Free text, never part of a storage key.</summary>
    public required string FileName { get; init; }

    /// <summary>The media type of the current content, as declared by the uploader.</summary>
    public required string ContentType { get; init; }

    /// <summary>The length of the current content, in bytes.</summary>
    public long ContentLength { get; init; }


    /// <summary>Lifecycle status.</summary>
    public DocumentStatus Status { get; init; } = DocumentStatus.Active;

    /// <summary>Opt-in: when true, a content change appends a <see cref="DocumentVersion"/> instead of overwriting in place.</summary>
    public bool IsVersioned { get; init; }

    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Creator subject.</summary>
    public required string CreatedBy { get; init; }

    /// <summary>Last modification timestamp, if any.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>Last modifier, if any.</summary>
    public string? ModifiedBy { get; init; }
}
