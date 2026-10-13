using System.Text.Json.Serialization;

namespace Stellaegis.Documents;

/// <summary>
/// A document as the HTTP API returns it: its index metadata, never its storage key nor its holder,
/// which the caller already addresses.
/// </summary>
/// <param name="DocumentUniqueId">Stable public identifier.</param>
/// <param name="DocumentType">Document type code declared at creation.</param>
/// <param name="FileName">File name of the current content.</param>
/// <param name="ContentType">Media type of the current content.</param>
/// <param name="ContentLength">Length of the current content, in bytes.</param>
/// <param name="ContentHash">SHA-256 (lowercase hex) of the current content.</param>
/// <param name="IsVersioned">Whether the document keeps a version history.</param>
/// <param name="Status">Lifecycle status.</param>
/// <param name="CreatedAt">Creation instant.</param>
/// <param name="CreatedBy">Creator subject.</param>
/// <param name="ModifiedAt">Last modification instant, if any.</param>
/// <param name="ModifiedBy">Last modifier, if any.</param>
public sealed record DocumentResponse(
    Guid DocumentUniqueId,
    string DocumentType,
    string FileName,
    string ContentType,
    long ContentLength,
    string ContentHash,
    bool IsVersioned,
    [property: JsonConverter(typeof(JsonStringEnumConverter<DocumentStatus>))] DocumentStatus Status,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? ModifiedAt,
    string? ModifiedBy)
{
    /// <summary>Projects a document for the API.</summary>
    public static DocumentResponse From(Document document) => new(
        document.DocumentUniqueId,
        document.DocumentType,
        document.FileName,
        document.ContentType,
        document.ContentLength,
        document.ContentHash,
        document.IsVersioned,
        document.Status,
        document.CreatedAt,
        document.CreatedBy,
        document.ModifiedAt,
        document.ModifiedBy);
}

/// <summary>A document revision as the HTTP API returns it.</summary>
/// <param name="VersionNumber">Revision number, from 1.</param>
/// <param name="FileName">File name of this revision.</param>
/// <param name="ContentType">Media type of this revision.</param>
/// <param name="ContentLength">Length of this revision, in bytes.</param>
/// <param name="CreatedAt">Creation instant.</param>
/// <param name="CreatedBy">Creator subject.</param>
public sealed record DocumentVersionResponse(
    int VersionNumber,
    string FileName,
    string ContentType,
    long ContentLength,
    DateTimeOffset CreatedAt,
    string CreatedBy)
{
    /// <summary>Projects a revision for the API.</summary>
    public static DocumentVersionResponse From(DocumentVersion version) => new(
        version.VersionNumber,
        version.FileName,
        version.ContentType,
        version.ContentLength,
        version.CreatedAt,
        version.CreatedBy);
}

/// <summary>One page of documents as the HTTP API returns it.</summary>
/// <param name="Documents">The documents of this page.</param>
/// <param name="ContinuationToken">The token for the next page, or <see langword="null"/> on the last one.</param>
public sealed record DocumentPageResponse(IReadOnlyList<DocumentResponse> Documents, string? ContinuationToken);
