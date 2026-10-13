using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stellaegis.Documents.PostgreSQL;

/// <summary>
/// Dependency injection helpers for the PostgreSQL document store.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL document store (<see cref="PostgreSqlDocumentStore"/>) and the document
    /// domain service layer. A blob adapter must also be registered.
    /// </summary>
    public static IServiceCollection AddStellaegisDocumentsPostgreSQL(
        this IServiceCollection services,
        Action<DocumentsDatabaseOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.TryAddSingleton<IDocumentStore, PostgreSqlDocumentStore>();
        services.AddStellaegisDocumentsServices();
        return services;
    }
}
