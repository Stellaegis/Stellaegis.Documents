namespace Stellaegis.Documents;

/// <summary>
/// Options of the document service.
/// </summary>
public sealed class DocumentsOptions
{
    /// <summary>Reference configuration section for these options.</summary>
    public const string SectionName = "Stellaegis:Documents";

    /// <summary>
    /// The first segment of the key of every blob holding document content, within the storage of each space:
    /// it keeps the documents apart from the other content of a store. Must be valid in a key of
    /// <c>BlobLocation</c> (Stellaegis.Blobs).
    /// </summary>
    public string KeyPrefix { get; init; } = "documents";

    /// <summary>The number of documents in one page of a list. Between 1 and 1000.</summary>
    public int ListPageSize { get; init; } = 100;
}
