namespace Stellaegis.Documents;

/// <summary>
/// Content handed to <see cref="IDocumentService"/>, with what the uploader knows about it. Its length is
/// measured by the service, never declared.
/// </summary>
public sealed record DocumentUpload
{
    /// <summary>The longest <see cref="FileName"/> accepted, in characters.</summary>
    public const int MaxFileNameLength = 255;

    /// <summary>The longest <see cref="ContentType"/> accepted, in characters.</summary>
    public const int MaxContentTypeLength = 255;

    /// <summary>The content, read to its end by the service. The caller keeps ownership and disposes it.</summary>
    public required Stream Content { get; init; }

    /// <summary>
    /// The file name the uploader gave the content, as is: free text, served back for download. It never
    /// reaches a storage key.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>The media type of the content, as declared by the uploader (e.g. <c>application/pdf</c>).</summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// The SHA-256 (hex, lowercase) the sender computed, or <see langword="null"/>. When given, the service
    /// refuses content whose digest differs: content damaged on its way is never stored.
    /// </summary>
    public string? ExpectedContentHash { get; init; }
}
