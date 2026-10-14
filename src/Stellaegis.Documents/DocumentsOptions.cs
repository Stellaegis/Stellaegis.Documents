namespace Stellaegis.Documents;

/// <summary>
/// Options of the document service.
/// </summary>
public sealed class DocumentsOptions
{
    /// <summary>Reference configuration section for these options.</summary>
    public const string SectionName = "Stellaegis:Documents";

    /// <summary>
    /// The blob name holding document content, within the storage of each space. Must be a valid name
    /// for <c>BlobLocation</c> (Stellaegis.Blobs): at most 63 characters.
    /// </summary>
    public string BlobSpace { get; init; } = "documents";

    /// <summary>The number of documents in one page of a list. Between 1 and 1000.</summary>
    public int ListPageSize { get; init; } = 100;
}
