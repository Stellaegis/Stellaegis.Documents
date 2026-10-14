using Stellaegis.Primitives;

namespace Stellaegis.Documents;

/// <summary>
/// Published when a document is created or its content changes. Carries only the document's index
/// metadata — its existence and identity, never business semantics — so a consumer can maintain its own
/// search/classification projection in its own database.
/// </summary>
/// <remarks>
/// Upsert semantics: a subscriber projects this snapshot keyed by <see cref="DocumentUniqueId"/>, whether
/// the document is new or its current content was replaced. Identical content re-sent is not a change and
/// publishes nothing.
/// </remarks>
public sealed record DocumentIndexedEvent : IEvent
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

    /// <summary>Stable public document identifier, constant across versions.</summary>
    public required Guid DocumentUniqueId { get; init; }

    /// <summary>Document type code declared by the consumer at creation.</summary>
    public required string DocumentType { get; init; }

    /// <summary>SHA-256 (lowercase hex) of the current content — lets a projection detect real changes.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Whether the document keeps a version history.</summary>
    public required bool IsVersioned { get; init; }
}
