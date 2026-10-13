namespace Stellaegis.Documents;

/// <summary>
/// Tenant-facing template resolution. Callers ask for a template by its system code; whether the
/// system default or a tenant override answers — and from which blob space the content is read — is
/// decided at runtime from the resolved row's tenant slug. The caller never expresses "system vs
/// tenant".
/// </summary>
public interface IDocumentTemplateService
{
    /// <summary>
    /// Resolves <paramref name="templateCode"/> for the ambient tenant (from <c>ITenantContext</c>).
    /// </summary>
    /// <returns>The resolved template, or <see langword="null"/> if none exists.</returns>
    Task<DocumentTemplate?> ResolveAsync(string templateCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves <paramref name="templateCode"/> for an explicit <paramref name="tenantSlug"/>
    /// (background workers; the slug must come from a trusted source, never user input).
    /// </summary>
    Task<DocumentTemplate?> ResolveAsync(string templateCode, string tenantSlug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves <paramref name="templateCode"/> for the ambient tenant and opens a readable stream
    /// over its content. The resolved row's tenant slug selects the blob space (system or tenant).
    /// </summary>
    /// <exception cref="TemplateNotFoundException">No template resolves for the code.</exception>
    Task<Stream> OpenContentAsync(string templateCode, CancellationToken cancellationToken = default);
}
