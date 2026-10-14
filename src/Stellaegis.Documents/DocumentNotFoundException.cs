using Stellaegis.Primitives;

namespace Stellaegis.Documents;

/// <summary>Thrown when a document cannot be found, or does not belong to the requested space.</summary>
/// <remarks>Carries <see cref="ErrorType.NotFound"/>, which the host translates into its own status.</remarks>
public sealed class DocumentNotFoundException(Guid documentUniqueId)
    : UserFacingException($"Document '{documentUniqueId:N}' not found.", ErrorType.NotFound)
{
    /// <summary>The requested document id.</summary>
    public Guid DocumentUniqueId { get; } = documentUniqueId;
}
