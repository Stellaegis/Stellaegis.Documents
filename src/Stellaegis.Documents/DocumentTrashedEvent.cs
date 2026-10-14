using Stellaegis.Primitives;

namespace Stellaegis.Documents;

/// <summary>
/// Published when a document is moved to the trash, so a consumer can drop it from its own projection.
/// </summary>
public sealed record DocumentTrashedEvent : IEvent
{
    /// <inheritdoc/>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// The compartment the document belongs to, or <see langword="null"/> exactly when
    /// <see cref="IsShared"/> is <see langword="true"/>.
    /// </summary>
    public Compartment? Compartment { get; init; }

    /// <summary>
    /// Whether the document belongs to the shared space. Its space is
    /// <c>IsShared ? Space.Shared : Space.Of(Compartment)</c>. The space travels as these two fields
    /// because <see cref="Space"/> has no serialized form, and a transport may serialize the event.
    /// </summary>
    public required bool IsShared { get; init; }

    /// <summary>Stable public document identifier.</summary>
    public required Guid DocumentUniqueId { get; init; }
}
