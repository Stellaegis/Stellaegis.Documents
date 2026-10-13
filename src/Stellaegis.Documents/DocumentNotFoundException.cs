using Stellaegis.Common;

namespace Stellaegis.Documents;

/// <summary>Thrown when a document cannot be found, or is not visible to the current tenant.</summary>
/// <remarks>Surfaces as an RFC 9457 <c>404 Not Found</c> via the global exception handler.</remarks>
public sealed class DocumentNotFoundException(Guid documentUniqueId)
    : UserFacingException($"Document '{documentUniqueId:N}' not found.", statusCode: 404)
{
    /// <summary>The requested document id.</summary>
    public Guid DocumentUniqueId { get; } = documentUniqueId;
}
