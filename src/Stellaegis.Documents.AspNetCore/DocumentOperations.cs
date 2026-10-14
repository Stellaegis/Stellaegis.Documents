using Stellaegis.Primitives.AspNetCore;

namespace Stellaegis.Documents.AspNetCore;

/// <summary>
/// The operations of the document API, one per route. The host keys its policies on their name
/// (<c>RequireAuthorizationPerOperation</c>), and receives them as the requirement of
/// <c>AuthorizeResourceAsync</c> on each document a route loads. Their kind follows the HTTP verb and says
/// nothing of who may perform them: the purge is a <see cref="OperationKind.Delete"/> like the trash.
/// </summary>
public static class DocumentOperations
{
    /// <summary>The module name every operation carries.</summary>
    public const string Module = "documents";

    /// <summary><c>POST documents</c>: creates a document.</summary>
    public static readonly Operation Create = new(Module, "create", OperationKind.Write);

    /// <summary><c>GET documents</c>: lists the active documents of the space, a page at a time.</summary>
    public static readonly Operation List = new(Module, "list", OperationKind.Read);

    /// <summary><c>GET documents/{id}</c>: reads a document's metadata.</summary>
    public static readonly Operation Read = new(Module, "read", OperationKind.Read);

    /// <summary><c>PUT documents/{id}/content</c>: replaces a document's content.</summary>
    public static readonly Operation ReplaceContent = new(Module, "replace-content", OperationKind.Write);

    /// <summary><c>GET documents/{id}/content</c>: reads a document's current content.</summary>
    public static readonly Operation ReadContent = new(Module, "read-content", OperationKind.Read);

    /// <summary><c>GET documents/{id}/versions</c>: lists a document's revisions.</summary>
    public static readonly Operation ListVersions = new(Module, "list-versions", OperationKind.Read);

    /// <summary><c>GET documents/{id}/versions/{n}/content</c>: reads the content of a revision.</summary>
    public static readonly Operation ReadVersionContent = new(Module, "read-version-content", OperationKind.Read);

    /// <summary><c>DELETE documents/{id}</c>: moves a document to the trash.</summary>
    public static readonly Operation Trash = new(Module, "trash", OperationKind.Delete);

    /// <summary><c>DELETE documents</c>: deletes every document of the compartment for good.</summary>
    public static readonly Operation Purge = new(Module, "purge", OperationKind.Delete);
}
