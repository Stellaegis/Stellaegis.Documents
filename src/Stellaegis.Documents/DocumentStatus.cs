namespace Stellaegis.Documents;

/// <summary>Lifecycle of a managed document (<c>doc.document_status</c>).</summary>
public enum DocumentStatus
{
    /// <summary>Live document.</summary>
    Active,

    /// <summary>Moved to the trash (schema <c>trash</c>, separate service).</summary>
    Trashed,
}
