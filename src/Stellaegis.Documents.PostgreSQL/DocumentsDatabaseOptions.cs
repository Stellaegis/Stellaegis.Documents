namespace Stellaegis.Documents.PostgreSQL;

/// <summary>
/// Options for the PostgreSQL document store (schema <c>doc</c>).
/// </summary>
public sealed class DocumentsDatabaseOptions
{
    /// <summary>Connection string to the database hosting the <c>doc</c> schema.</summary>
    public string ConnectionString { get; set; } = "";
}
