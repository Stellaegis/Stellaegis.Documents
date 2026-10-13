using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.Common;
using Stellaegis.Documents.InMemory;

namespace Stellaegis.Documents;

/// <summary>
/// Dependency injection helpers for the Stellaegis.Documents domain.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the document domain service layer (<see cref="IDocumentTemplateService"/>) and the
    /// <see cref="SystemTemplateSeeder"/>. Requires an <see cref="ITemplateStore"/> and an
    /// <c>IBlobStore</c> to be registered (via an InMemory/PostgreSQL store and a blob adapter).
    /// </summary>
    /// <remarks>Called automatically by <see cref="AddStellaegisDocumentsInMemory"/> and the PostgreSQL variant.</remarks>
    public static IServiceCollection AddStellaegisDocumentsServices(this IServiceCollection services)
    {
        services.TryAddScoped<IDocumentTemplateService, DocumentTemplateService>();
        services.TryAddScoped<IDocumentService, DocumentService>();

        services.TryAddScoped<SystemTemplateSeeder>();
        services.AddScoped<IDataSeeder, SystemTemplateSeeder>(sp => sp.GetRequiredService<SystemTemplateSeeder>());

        return services;
    }

    /// <summary>
    /// Registers the in-memory <see cref="ITemplateStore"/> along with the document domain service layer.
    /// For development and testing only.
    /// </summary>
    public static IServiceCollection AddStellaegisDocumentsInMemory(this IServiceCollection services)
    {
        services.TryAddSingleton<ITemplateStore, InMemoryTemplateStore>();
        services.TryAddSingleton<IDocumentStore, InMemoryDocumentStore>();
        services.AddStellaegisDocumentsServices();
        return services;
    }

    /// <summary>
    /// Contributes a system template (<c>tenant_slug = ''</c>) to be seeded at startup by
    /// <see cref="SystemTemplateSeeder"/>.
    /// </summary>
    public static IServiceCollection AddStellaegisSystemTemplate(this IServiceCollection services, SystemTemplateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        services.AddSingleton(definition);
        return services;
    }
}
