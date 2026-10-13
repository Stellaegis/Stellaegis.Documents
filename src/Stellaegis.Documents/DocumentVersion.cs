namespace Stellaegis.Documents;

/// <summary>
/// A stored revision of a versioned document. Double key (<see cref="DocumentUniqueId"/> +
/// <see cref="VersionNumber"/>): the document keeps its stable public identity, only the revision
/// changes. Carries only its own blob key and audit — no content hash (change detection compares the
/// incoming content against the current pointer only) and no free-text note (révision semantics
/// belong to the tenant's own store, not the GED).
/// </summary>
public sealed record DocumentVersion
{
    /// <summary>The document this revision belongs to.</summary>
    public Guid DocumentUniqueId { get; init; }

    /// <summary>Revision number (1-based, increasing).</summary>
    public int VersionNumber { get; init; }

    /// <summary>Logical blob key of this revision's content (never overwritten in place).</summary>
    public required string StoragePath { get; init; }

    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Creator subject.</summary>
    public required string CreatedBy { get; init; }
}
