using Stellaegis.Blobs;
using Stellaegis.MultiTenant.Context;

namespace Stellaegis.Documents;

/// <summary>
/// Default <see cref="IDocumentTemplateService"/>: resolves the ambient tenant from
/// <see cref="ITenantContext"/> and reads content through <see cref="IBlobStore"/>, the resolved
/// row's tenant slug selecting the blob space (system or tenant).
/// </summary>
internal sealed class DocumentTemplateService(
    ITemplateStore store,
    ITenantContext tenantContext,
    IBlobStore blobStore) : IDocumentTemplateService
{
    private readonly ITemplateStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ITenantContext _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
    private readonly IBlobStore _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));

    public Task<DocumentTemplate?> ResolveAsync(string templateCode, CancellationToken cancellationToken = default)
        => _store.ResolveAsync(templateCode, CurrentTenantSlug(), cancellationToken);

    public Task<DocumentTemplate?> ResolveAsync(string templateCode, string tenantSlug, CancellationToken cancellationToken = default)
        => _store.ResolveAsync(templateCode, tenantSlug, cancellationToken);

    public async Task<Stream> OpenContentAsync(string templateCode, CancellationToken cancellationToken = default)
    {
        var tenantSlug = CurrentTenantSlug();
        var template = await _store.ResolveAsync(templateCode, tenantSlug, cancellationToken).ConfigureAwait(false)
            ?? throw new TemplateNotFoundException(templateCode, tenantSlug);

        // The resolved row's tenant slug ('' for a system default) selects the blob space.
        var location = new BlobLocation(template.TenantSlug, BlobNames.Templates, template.StoragePath);
        return await _blobStore.OpenReadAsync(location, cancellationToken).ConfigureAwait(false);
    }

    private string CurrentTenantSlug()
    {
        if (!_tenantContext.TryGetTenantSlug(out var tenantSlug))
            throw new InvalidOperationException(
                "No tenant context available. Use the overload that accepts an explicit tenant slug for background workers.");

        return tenantSlug;
    }
}
