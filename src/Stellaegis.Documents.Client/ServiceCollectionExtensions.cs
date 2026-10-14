using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stellaegis.Primitives;

namespace Stellaegis.Documents.Client;

/// <summary>
/// Dependency injection helpers for Stellaegis.Documents.Client.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDocumentService"/> backed by an HTTP client of the document API, configured
    /// from <paramref name="sectionName"/>.
    /// </summary>
    /// <remarks>
    /// The client sends requests as they are: authenticating them is the caller's concern. Add a
    /// <see cref="DelegatingHandler"/> to the returned builder to attach a token, or anything else the host
    /// requires.
    /// </remarks>
    /// <example>
    /// appsettings.json, for a host that maps the endpoints under <c>orgs/{org}</c> and <c>shared</c>:
    /// <code>
    /// "Stellaegis": {
    ///   "Documents": {
    ///     "Client": {
    ///       "CompartmentUrl": "https://documents.example.com/api/orgs/{compartment}/",
    ///       "SharedUrl": "https://documents.example.com/api/shared/"
    ///     }
    ///   }
    /// }
    /// </code>
    /// Program.cs:
    /// <code>
    /// builder.Services.AddStellaegisDocumentsClient(builder.Configuration)
    ///     .AddHttpMessageHandler&lt;MyTokenHandler&gt;();
    /// </code>
    /// </example>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration holding <paramref name="sectionName"/>.</param>
    /// <param name="sectionName">The configuration section of the client options.</param>
    /// <returns>The builder of the underlying HTTP client, to add handlers to.</returns>
    public static IHttpClientBuilder AddStellaegisDocumentsClient(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = DocumentsClientOptions.SectionName)
    {
        Guard.NotNull(configuration);
        Guard.NotWhiteSpace(sectionName);

        services.Configure<DocumentsClientOptions>(configuration.GetSection(sectionName));
        return services.AddHttpClient<IDocumentService, HttpDocumentService>((sp, http) =>
            http.Timeout = sp.GetRequiredService<IOptions<DocumentsClientOptions>>().Value.Timeout);
    }
}
