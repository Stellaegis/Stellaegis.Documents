using Stellaegis.Primitives;

namespace Stellaegis.Documents;

/// <summary>
/// The identity of the document module, <c>documents</c>: the routes of <c>Stellaegis.Documents.AspNetCore</c>
/// belong to it.
/// </summary>
public sealed class DocumentsModule : IStellaegisModule
{
    /// <inheritdoc />
    public static string Name => "documents";
}
