using Stellaegis.Authorization;

namespace Stellaegis.Documents.AspNetCore;

/// <summary>
/// Declares the framework Documents (GED) API by its immutable api_code (<c>"documents"</c>), under which
/// its <c>user:documents.read/write</c> permissions are evaluated.
/// </summary>
public sealed class DocumentsApi : IStellaegisApi
{
    /// <inheritdoc />
    public static string ApiCode => "documents";
}
