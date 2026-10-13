using Stellaegis.Primitives;

namespace Stellaegis.Documents;

/// <summary>
/// Published when a document is moved to the trash, so a consumer can drop it from its own projection.
/// </summary>
public sealed record DocumentTrashedEvent : IEvent
{
    /// <inheritdoc/>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The holder the document belongs to, or the empty string for the platform.</summary>
    public required string Holder { get; init; }

    /// <summary>Stable public document identifier.</summary>
    public required Guid DocumentUniqueId { get; init; }
}
