using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.Documents.InMemory;
using Stellaegis.Primitives;

namespace Stellaegis.Documents;

/// <summary>
/// Dependency injection helpers for the Stellaegis.Documents domain.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the document domain service layer (<see cref="IDocumentService"/>), configured from
    /// <paramref name="sectionName"/>. Requires an <see cref="IDocumentStore"/> and an <c>IBlobStore</c>
    /// to be registered (via an InMemory/PostgreSQL store and a blob adapter).
    /// </summary>
    /// <remarks>Called automatically by <see cref="AddStellaegisDocumentsInMemory"/> and the PostgreSQL variant.</remarks>
    public static IServiceCollection AddStellaegisDocumentsServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = DocumentsOptions.SectionName)
    {
        Guard.NotNull(configuration);
        Guard.NotWhiteSpace(sectionName);

        services.Configure<DocumentsOptions>(configuration.GetSection(sectionName));
        services.TryAddScoped<IDocumentService, DocumentService>();
        return services;
    }

    /// <summary>
    /// Registers the in-memory <see cref="IDocumentStore"/> along with the document domain service layer.
    /// For development and testing only.
    /// </summary>
    public static IServiceCollection AddStellaegisDocumentsInMemory(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = DocumentsOptions.SectionName)
    {
        services.TryAddSingleton<IDocumentStore, InMemoryDocumentStore>();
        services.AddStellaegisDocumentsServices(configuration, sectionName);
        return services;
    }
}
