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
    /// appsettings.json:
    /// <code>
    /// "Stellaegis": {
    ///   "Documents": {
    ///     "Client": { "BaseUrl": "https://documents.example.com/api/" }
    ///   }
    /// }
    /// </code>
    /// Program.cs, for a host that maps the endpoints under <c>t/{tenantSlug}</c>:
    /// <code>
    /// builder.Services.AddStellaegisDocumentsClient(builder.Configuration, holder => $"t/{holder}/")
    ///     .AddHttpMessageHandler&lt;MyTokenHandler&gt;();
    /// </code>
    /// </example>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration holding <paramref name="sectionName"/>.</param>
    /// <param name="holderPath">
    /// Gives the path of a holder's endpoints, relative to the base URL. Omitted, the holder is not part of
    /// the URL: the host resolves it some other way, or does not isolate.
    /// </param>
    /// <param name="sectionName">The configuration section of the client options.</param>
    /// <returns>The builder of the underlying HTTP client, to add handlers to.</returns>
    public static IHttpClientBuilder AddStellaegisDocumentsClient(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<string, string>? holderPath = null,
        string sectionName = DocumentsClientOptions.SectionName)
    {
        Guard.NotNull(configuration);
        Guard.NotWhiteSpace(sectionName);

        services.Configure<DocumentsClientOptions>(configuration.GetSection(sectionName));
        services.AddSingleton(holderPath is null ? DocumentsHolderPath.None : new DocumentsHolderPath(holderPath));

        return services.AddHttpClient<IDocumentService, HttpDocumentService>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<DocumentsClientOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = options.Timeout;
        });
    }
}
