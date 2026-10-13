namespace Stellaegis.Documents;

/// <summary>
/// Options of the document service.
/// </summary>
public sealed class DocumentsOptions
{
    /// <summary>Reference configuration section for these options.</summary>
    public const string SectionName = "Stellaegis:Documents";

    /// <summary>
    /// The blob space holding document content, within each holder's storage. Must be a valid space
    /// name for <c>BlobLocation</c> (Stellaegis.Blobs): at most 63 characters.
    /// </summary>
    public string BlobSpace { get; set; } = "documents";
}
