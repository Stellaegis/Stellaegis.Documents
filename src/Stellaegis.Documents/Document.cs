namespace Stellaegis.Documents;

/// <summary>
/// GED index entry: the existence and identity of a document — never its content (which lives in the
/// blob store) nor its business semantics. <see cref="StoragePath"/>/<see cref="ContentHash"/> always
/// point at the CURRENT version (denormalised pointer — one GUID, one read, the document); the full
/// history lives in <see cref="DocumentVersion"/>.
/// </summary>
public sealed record Document
{
    /// <summary>Stable public GUID — published in backplane events and referenced by the tenant's projection.</summary>
    public Guid DocumentUniqueId { get; init; }

    /// <summary>Owning tenant slug.</summary>
    public required string TenantSlug { get; init; }

    /// <summary>Application-defined document type code.</summary>
    public required string DocumentType { get; init; }

    /// <summary>Logical blob key of the current content, within the tenant's <c>ged</c> space.</summary>
    public required string StoragePath { get; init; }

    /// <summary>SHA-256 (hex, lowercase) of the current content.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Opaque GUID to the tenant's business entity. The GED never interprets it.</summary>
    public Guid? BusinessObjectLink { get; init; }

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
