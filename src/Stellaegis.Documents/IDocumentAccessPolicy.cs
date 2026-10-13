namespace Stellaegis.Documents;

/// <summary>The operation an actor attempts on a managed document.</summary>
public enum DocumentAction
{
    /// <summary>Read metadata or download content (current or a past version).</summary>
    Read,

    /// <summary>Replace content (new version or in-place overwrite).</summary>
    Write,

    /// <summary>Move the document to the trash.</summary>
    Delete,
}

/// <summary>
/// A request to authorize <see cref="Actor"/> performing <see cref="Action"/> on a specific
/// <see cref="Document"/> within <see cref="TenantSlug"/>.
/// </summary>
/// <param name="TenantSlug">The tenant whose space the document belongs to.</param>
/// <param name="Actor">The acting subject — the <c>sub</c> claim of the caller's token.</param>
/// <param name="Action">The attempted operation.</param>
/// <param name="Document">The resolved document, including its opaque <c>BusinessObjectLink</c>.</param>
public sealed record DocumentAccessRequest(
    string TenantSlug,
    string Actor,
    DocumentAction Action,
    Document Document);

/// <summary>
/// Per-document (data-driven) authorization hook. The host consults it after the tenant-level
/// <c>user:documents.*</c> permission has passed, to decide whether <i>this</i> actor may act on
/// <i>this</i> document — the question the GED itself cannot answer, since it indexes a document's
/// existence and identity, never its business semantics.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation (<see cref="AllowAllDocumentAccessPolicy"/>) authorizes everything,
/// preserving tenant-grain access. A developer needing finer grain registers his own implementation
/// <b>before</b> calling <c>AddStellaegisDocumentsApi</c>, typically resolving the document's opaque
/// <c>BusinessObjectLink</c> against his own business database (e.g. via <c>ITenantDbConnectionFactory</c>).
/// </para>
/// <para>
/// This hook governs <b>point access</b> only — one document at a time. Business listing/search
/// (« all documents of this client », « quotes to validate ») is not a GED concern: it is answered
/// from the developer's own projection (built from the <c>DocumentIndexed</c> backplane event), where
/// the query itself encodes the authorization. The GED never filters a business list.
/// </para>
/// </remarks>
public interface IDocumentAccessPolicy
{
    /// <summary>
    /// Returns whether the request is authorized. A denied request is surfaced by the host as
    /// <c>404 Not Found</c> rather than <c>403</c>, so a caller cannot probe a document's existence.
    /// </summary>
    ValueTask<bool> IsAllowedAsync(DocumentAccessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IDocumentAccessPolicy"/>: authorizes every request, deferring entirely to the
/// tenant-level <c>user:documents.*</c> permission already enforced by the host.
/// </summary>
/// <remarks>
/// This is the coarsest grain — every user holding the tenant permission can access every document of
/// the tenant, a valid model for a SaaS where documents are shared within the tenant. A developer that
/// needs per-document authorization registers his own <see cref="IDocumentAccessPolicy"/>. When this
/// default is in effect the host logs a startup warning, so the coarse grain is never a silent surprise.
/// </remarks>
public sealed class AllowAllDocumentAccessPolicy : IDocumentAccessPolicy
{
    /// <inheritdoc />
    public ValueTask<bool> IsAllowedAsync(DocumentAccessRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);
}
