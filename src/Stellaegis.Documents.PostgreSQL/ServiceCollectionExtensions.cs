using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.Primitives;

namespace Stellaegis.Documents.PostgreSQL;

/// <summary>
/// Dependency injection helpers for the PostgreSQL document store.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL document store, configured from <paramref name="sectionName"/>, and the
    /// document domain service layer, configured from its own section. A blob adapter must also be
    /// registered.
    /// </summary>
    public static IServiceCollection AddStellaegisDocumentsPostgreSQL(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = DocumentsDatabaseOptions.SectionName)
    {
        Guard.NotNull(configuration);
        Guard.NotWhiteSpace(sectionName);

        services.Configure<DocumentsDatabaseOptions>(configuration.GetSection(sectionName));
        services.TryAddSingleton<IDocumentStore, PostgreSqlDocumentStore>();
        services.AddStellaegisDocumentsServices(configuration);
        return services;
    }
}
