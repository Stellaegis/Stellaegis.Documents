namespace Stellaegis.Documents;

/// <summary>
/// Lifecycle of a document template (<c>doc.template_status</c>).
/// </summary>
public enum TemplateStatus
{
    /// <summary>Resolved normally.</summary>
    Active,

    /// <summary>Still served for compatibility (existing documents depend on it) but no longer offered for new uses. Excluded from resolution.</summary>
    Deprecated,

    /// <summary>No longer resolved; kept for audit/history only.</summary>
    Archived,
}
