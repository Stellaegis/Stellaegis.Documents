using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Blobs.FileSystem;
using Stellaegis.Documents.AspNetCore;
using Stellaegis.Documents.Client;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;

namespace Stellaegis.Documents.Tests;

/// <summary>
/// An in-memory host mapping the document API under <c>/api/t/{compartment}</c>, as Stellaegis routes its tenants:
/// every operation for any authenticated caller but the purge, kept for the <c>admin</c> role, and an
/// authorization handler that refuses <c>mallory</c> every document. The caller's identity is a header, set by <see cref="Http"/> and
/// <see cref="Client"/>.
/// </summary>
public sealed class DocumentsHost : IAsyncLifetime
{
    private readonly string _blobRoot = Path.Combine(Path.GetTempPath(), "stellaegis-documents-tests", Guid.NewGuid().ToString("N"));
    private WebApplication? _app;

    private TestServer Server => _app?.GetTestServer() ?? throw new InvalidOperationException("The host is not started.");

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration["Stellaegis:Documents:ListPageSize"] = "2";

        builder.Services.AddStellaegisFileSystemBlobStore(o => o.RootPath = _blobRoot);
        builder.Services.AddStellaegisDocumentsInMemory(builder.Configuration);
        builder.Services.AddSingleton<IAuthorizationHandler, RefuseMallory>();
        builder.Services.AddAuthentication(HeaderIdentity.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderIdentity>(HeaderIdentity.SchemeName, null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy("member", p => p.RequireAuthenticatedUser());
            o.AddPolicy("admin", p => p.RequireClaim("role", "admin"));
        });

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGroup("/api/t/{compartment}")
            .MapDocumentsEndpoints(http => Compartment.TryParse(http.GetRouteValue("compartment") as string, out var c) ? c : null)
            .RequireAuthorizationPerOperation(op => op == DocumentOperations.Purge ? "admin" : "member");
        await _app.StartAsync();
    }

    /// <summary>The routes the host serves, conventions applied.</summary>
    public IReadOnlyList<RouteEndpoint> Endpoints
        => ((IEndpointRouteBuilder)(_app ?? throw new InvalidOperationException("The host is not started."))).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();

    /// <summary>An HTTP client to the host, as <paramref name="subject"/> (anonymous when null).</summary>
    public HttpClient Http(string? subject, string? role = null)
    {
        var http = Server.CreateClient();
        if (subject is not null)
            http.DefaultRequestHeaders.Add(HeaderIdentity.SubjectHeader, subject);
        if (role is not null)
            http.DefaultRequestHeaders.Add(HeaderIdentity.RoleHeader, role);
        return http;
    }

    /// <summary>
    /// The document client as <paramref name="subject"/>, its requests carried to the host. With
    /// <paramref name="damage"/>, the first byte of every downloaded content is flipped on its way.
    /// </summary>
    public IDocumentService Client(string subject, string? role = null, bool damage = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([KeyValuePair.Create<string, string?>("Stellaegis:Documents:Client:BaseUrl", "http://localhost/api/")])
            .Build();

        var services = new ServiceCollection();
        services.AddStellaegisDocumentsClient(configuration, compartment => $"t/{compartment}/")
            .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler())
            .AddHttpMessageHandler(() => new Identity(subject, role))
            .AddHttpMessageHandler(() => new Damage(damage));
        return services.BuildServiceProvider().GetRequiredService<IDocumentService>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        if (Directory.Exists(_blobRoot))
            Directory.Delete(_blobRoot, recursive: true);
    }

    private sealed class RefuseMallory : AuthorizationHandler<Operation, Document>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, Operation requirement, Document resource)
        {
            if (context.User.FindFirst("sub")?.Value != "mallory")
                context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class HeaderIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "header";
        public const string SubjectHeader = "X-Test-Subject";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(SubjectHeader, out var subject))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new("sub", subject.ToString()) };
            if (Request.Headers.TryGetValue(RoleHeader, out var role))
                claims.Add(new Claim("role", role.ToString()));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private sealed class Identity(string subject, string? role) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add(HeaderIdentity.SubjectHeader, subject);
            if (role is not null)
                request.Headers.Add(HeaderIdentity.RoleHeader, role);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Damage(bool on) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!on || !request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal))
                return response;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > 0)
                bytes[0] ^= 0xFF;
            var damaged = new ByteArrayContent(bytes);
            foreach (var header in response.Content.Headers)
                damaged.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = damaged;
            return response;
        }
    }
}
