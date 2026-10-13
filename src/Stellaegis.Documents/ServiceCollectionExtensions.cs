using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.Documents.InMemory;

namespace Stellaegis.Documents;

/// <summary>
/// Dependency injection helpers for the Stellaegis.Documents domain.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the document domain service layer (<see cref="IDocumentService"/>). Requires an
    /// <see cref="IDocumentStore"/> and an <c>IBlobStore</c> to be registered (via an InMemory/PostgreSQL
    /// store and a blob adapter).
    /// </summary>
    /// <remarks>Called automatically by <see cref="AddStellaegisDocumentsInMemory"/> and the PostgreSQL variant.</remarks>
    public static IServiceCollection AddStellaegisDocumentsServices(
        this IServiceCollection services,
        Action<DocumentsOptions>? configure = null)
    {
        var options = services.AddOptions<DocumentsOptions>();
        if (configure is not null)
            options.Configure(configure);

        services.TryAddScoped<IDocumentService, DocumentService>();
        return services;
    }

    /// <summary>
    /// Registers the in-memory <see cref="IDocumentStore"/> along with the document domain service layer.
    /// For development and testing only.
    /// </summary>
    public static IServiceCollection AddStellaegisDocumentsInMemory(
        this IServiceCollection services,
        Action<DocumentsOptions>? configure = null)
    {
        services.TryAddSingleton<IDocumentStore, InMemoryDocumentStore>();
        services.AddStellaegisDocumentsServices(configure);
        return services;
    }
}
