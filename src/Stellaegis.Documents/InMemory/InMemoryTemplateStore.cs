using System.Collections.Concurrent;

namespace Stellaegis.Documents.InMemory;

/// <summary>
/// In-memory <see cref="ITemplateStore"/> for development and tests. Keyed by
/// <c>(templateCode, tenantSlug)</c> — the same uniqueness as <c>doc.templates</c>.
/// </summary>
internal sealed class InMemoryTemplateStore : ITemplateStore
{
    private readonly ConcurrentDictionary<(string Code, string Slug), DocumentTemplate> _templates = new();

    public Task<DocumentTemplate?> ResolveAsync(string templateCode, string tenantSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        ArgumentNullException.ThrowIfNull(tenantSlug);

        // Tenant override wins over the system default; only active rows resolve.
        if (tenantSlug.Length != 0 &&
            _templates.TryGetValue((templateCode, tenantSlug), out var custom) &&
            custom.Status == TemplateStatus.Active)
        {
            return Task.FromResult<DocumentTemplate?>(custom);
        }

        if (_templates.TryGetValue((templateCode, ""), out var system) &&
            system.Status == TemplateStatus.Active)
        {
            return Task.FromResult<DocumentTemplate?>(system);
        }

        return Task.FromResult<DocumentTemplate?>(null);
    }

    public Task<DocumentTemplate?> GetAsync(string templateCode, string tenantSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        ArgumentNullException.ThrowIfNull(tenantSlug);

        _templates.TryGetValue((templateCode, tenantSlug), out var template);
        return Task.FromResult(template);
    }

    public Task CreateAsync(DocumentTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (!_templates.TryAdd((template.TemplateCode, template.TenantSlug), template))
            throw new InvalidOperationException(
                $"A template already exists for code '{template.TemplateCode}' and tenant '{template.TenantSlug}'.");

        return Task.CompletedTask;
    }
}
