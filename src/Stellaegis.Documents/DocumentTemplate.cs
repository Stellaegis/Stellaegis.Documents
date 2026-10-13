namespace Stellaegis.Documents;

/// <summary>
/// A document template: a framework system default (<see cref="TenantSlug"/> = empty sentinel)
/// or a tenant override. System rows and overrides cohabit in <c>doc.templates</c> and are
/// resolved in a single query — see <see cref="ITemplateStore.ResolveAsync"/>.
/// </summary>
public sealed record DocumentTemplate
{
    /// <summary>Stable identifier (primary key).</summary>
    public Guid TemplateUniqueId { get; init; }

    /// <summary>Canonical code requested by callers (e.g. <c>standard_invoice</c>).</summary>
    public required string TemplateCode { get; init; }

    /// <summary>
    /// Owning tenant slug, or the empty-string sentinel for a framework system default.
    /// </summary>
    public string TenantSlug { get; init; } = "";

    /// <summary>
    /// Logical blob key of the template content within its space (resolved against
    /// <c>BlobNames.Templates</c>) — never a physical/absolute path.
    /// </summary>
    public required string StoragePath { get; init; }

    /// <summary>Whether a tenant may create an override for this <see cref="TemplateCode"/>.</summary>
    public bool IsCustomizable { get; init; } = true;

    /// <summary>Render engine discriminant (default <c>scriban</c>).</summary>
    public string RenderEngineCode { get; init; } = "scriban";

    /// <summary>Lifecycle status.</summary>
    public TemplateStatus Status { get; init; } = TemplateStatus.Active;

    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Creator (subject or "bootstrap").</summary>
    public required string CreatedBy { get; init; }

    /// <summary>Last modification timestamp, if any.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>Last modifier, if any.</summary>
    public string? ModifiedBy { get; init; }

    /// <summary><see langword="true"/> when this is a framework system default.</summary>
    public bool IsSystem => TenantSlug.Length == 0;
}
