using Microsoft.Extensions.Logging;
using Stellaegis.Common;

namespace Stellaegis.Documents;

/// <summary>
/// Seeds system templates (<c>tenant_slug = ''</c>) from the <see cref="SystemTemplateDefinition"/>
/// instances registered in DI. Idempotent — existing rows are left untouched, so resolution always
/// finds a system row without an application-level fallback.
/// </summary>
internal sealed class SystemTemplateSeeder(
    IEnumerable<SystemTemplateDefinition> definitions,
    ITemplateStore store,
    ILogger<SystemTemplateSeeder> logger) : IDataSeeder
{
    public int Order => 40;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var definition in definitions)
        {
            var existing = await store.GetAsync(definition.TemplateCode, "", cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                logger.LogDebug("System template '{TemplateCode}' already exists. Skipping.", definition.TemplateCode);
                continue;
            }

            await store.CreateAsync(new DocumentTemplate
            {
                TemplateUniqueId = Guid.NewGuid(),
                TemplateCode = definition.TemplateCode,
                TenantSlug = "",
                StoragePath = definition.StoragePath,
                IsCustomizable = definition.IsCustomizable,
                RenderEngineCode = definition.RenderEngineCode,
                Status = TemplateStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = "bootstrap",
            }, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Seeded system template '{TemplateCode}'.", definition.TemplateCode);
        }
    }
}
