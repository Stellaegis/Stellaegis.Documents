using Stellaegis.Primitives;

namespace Stellaegis.Documents;

/// <summary>
/// The actors the module opens a session under on its own account, rather than under the actor of the call.
/// </summary>
public static class DocumentActors
{
    /// <summary>
    /// <c>admin:purge</c>: the actor of the session of a purge, which <c>owner-isolation.sql</c> lets through so
    /// that a purge empties the whole compartment and not only the documents of its caller. The caller of
    /// <see cref="IDocumentService.PurgeCompartmentAsync"/> stays the one the module logs.
    /// </summary>
    public static Actor Purge { get; } = Actor.Parse("admin:purge");
}
