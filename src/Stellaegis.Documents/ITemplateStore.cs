namespace Stellaegis.Documents;

/// <summary>
/// Persistence contract for <see cref="DocumentTemplate"/> (table <c>doc.templates</c>).
/// </summary>
public interface ITemplateStore
{
    /// <summary>
    /// Resolves the effective template for <paramref name="templateCode"/> in the context of
    /// <paramref name="tenantSlug"/>, applying the standard-or-custom rule in a single lookup:
    /// the tenant override wins if it exists, otherwise the system default (empty-slug sentinel)
    /// answers. Only <see cref="TemplateStatus.Active"/> rows are considered.
    /// </summary>
    /// <returns>The resolved template, or <see langword="null"/> if neither an override nor a system default exists.</returns>
    Task<DocumentTemplate?> ResolveAsync(string templateCode, string tenantSlug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the exact row for <paramref name="templateCode"/> and <paramref name="tenantSlug"/>
    /// (any status), without the standard-or-custom fallback. Used for idempotent seeding.
    /// </summary>
    Task<DocumentTemplate?> GetAsync(string templateCode, string tenantSlug, CancellationToken cancellationToken = default);

    /// <summary>Inserts a new template row.</summary>
    Task CreateAsync(DocumentTemplate template, CancellationToken cancellationToken = default);
}
