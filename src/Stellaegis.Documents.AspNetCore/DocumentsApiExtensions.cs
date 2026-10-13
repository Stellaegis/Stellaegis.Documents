using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.AspNetCore;
using Stellaegis.Authorization;
using Stellaegis.Authorization.AspNetCore;
using Stellaegis.Authorization.Client;
using Stellaegis.Backplane.DocumentEvents;
using Stellaegis.Http;
using Stellaegis.MultiTenant;
using Stellaegis.Security.UserBlocking;

namespace Stellaegis.Documents.AspNetCore;

/// <summary>
/// Extension methods that fully configure the Stellaegis Documents (GED) <b>content</b> API host:
/// JWT Bearer (user) authentication, Access-backed <c>user:documents.read/write</c> policies,
/// <c>/t/{tenantSlug}</c> tenant resolution, upload/download/versioning/trash endpoints, and a
/// health check. The developer registers a document store and a blob adapter <b>before</b> calling
/// <see cref="AddStellaegisDocumentsApi"/>.
/// </summary>
/// <remarks>
/// This is the user-facing, tenant-scoped half of the documents domain. The M2M template-rendering
/// half is a separate host. Content delivery streams from the host by default, or — when the blob
/// adapter can pre-sign — redirects the browser straight to the storage backend.
/// </remarks>
/// <example>
/// <code>
/// var builder = WebApplication.CreateBuilder(args);
/// builder.Services.AddStellaegisInMemoryBackplane();
/// builder.Services.AddStellaegisDocumentsInMemory();     // or AddStellaegisDocumentsPostgreSQL
/// builder.Services.AddFileSystemBlobStore(...);          // or another IBlobStore adapter
/// builder.AddStellaegisDocumentsApi();
///
/// var app = builder.Build();
/// app.UseStellaegisDocumentsApi();
/// app.Run();
/// </code>
/// </example>
public static class DocumentsApiExtensions
{
    /// <summary>Policy guarding read operations (resolves to atom <c>documents.read</c>).</summary>
    public const string ReadPolicy = "user:documents.read";

    /// <summary>Policy guarding write operations (resolves to atom <c>documents.write</c>).</summary>
    public const string WritePolicy = "user:documents.write";

    private const string HealthPath = "/api/documents/health";
    private static readonly TimeSpan DownloadUrlValidity = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Registers all services required by the Documents content API: JWT Bearer authentication,
    /// the Access authorization client with dynamic <c>user:</c> policies, tenant resolution,
    /// CORS, JSON, rate limiting, user-security, and the GED backplane events.
    /// </summary>
    /// <remarks>
    /// Register the document store (<c>AddStellaegisDocumentsInMemory</c>/<c>...PostgreSQL</c>),
    /// a blob adapter, and a backplane transport (<c>AddStellaegisInMemoryBackplane</c> or the client)
    /// <b>before</b> calling this method.
    /// </remarks>
    public static WebApplicationBuilder AddStellaegisDocumentsApi(this WebApplicationBuilder builder)
    {
        // ── Authentication (JWT Bearer, user tokens) ─────────────
        builder.Services.AddStellaegisAuthentication(builder.Configuration, builder.Environment);

        // ── Authorization (Access client + dynamic user: policies) ─
        builder.Services.AddStellaegisM2M(builder.Configuration);
        builder.Services.AddStellaegisHttpAuthorizationClients(builder.Configuration);
        builder.Services.AddStellaegisAuthorizationPolicies();

        // ── Tenant resolution (/t/{tenantSlug}) ──────────────────
        // The health endpoint is not tenant-scoped, so it must bypass strict resolution.
        builder.Services.AddTenantSupport(o => o.ExcludedPathPrefixes.Add(new PathString(HealthPath)));

        // ── Cross-cutting ────────────────────────────────────────
        builder.Services.AddStellaegisCors(builder.Configuration, builder.Environment);
        builder.Services.AddStellaegisJsonOptions();
        builder.Services.AddStellaegisRateLimiter(builder.Configuration);
        builder.Services.AddUserSecurity();
        builder.Services.AddStellaegisSeeding();

        // ── Backplane event contracts (documents.indexed/trashed) ─
        builder.Services.AddStellaegisDocumentsBackplaneEvents();

        // ── Per-document authorization (point access) ────────────
        // Default = tenant-grain (defers to the user:documents.* permission). A developer needing
        // data-driven, per-document authorization registers his own IDocumentAccessPolicy before
        // calling this method. The advisor warns at startup when the permissive default is in effect.
        builder.Services.TryAddScoped<IDocumentAccessPolicy, AllowAllDocumentAccessPolicy>();
        builder.Services.AddHostedService<DocumentAccessPolicyAdvisor>();

        return builder;
    }

    /// <summary>
    /// Configures the middleware pipeline and maps the Documents content endpoints.
    /// </summary>
    public static WebApplication UseStellaegisDocumentsApi(this WebApplication app)
    {
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });

        app.UseStellaegisExceptionHandler();
        app.UseStellaegisSecurityHeaders();
        app.UseRateLimiter();
        app.UseCors();
        app.UseAuthentication();
        app.UseUserSecurity();
        app.UseTenantResolution();
        app.UseAuthorization();

        app.MapStellaegisHealth();
        app.MapStellaegisDocumentsEndpoints();

        return app;
    }

    /// <summary>
    /// Maps the Documents content endpoints without configuring the middleware pipeline.
    /// Use this when composing several APIs in a single host.
    /// </summary>
    public static WebApplication MapStellaegisDocumentsEndpoints(this WebApplication app)
    {
        app.MapGet(HealthPath, () => Results.Ok(new
            {
                api = "documents",
                status = "healthy",
                timestamp = DateTimeOffset.UtcNow
            }))
            .WithName("HealthCheck_Documents")
            .AllowAnonymous();

        var group = app.MapGroup("/api/documents/t/{tenantSlug}");
        group.WithStellaegisApi<DocumentsApi>();

        // ── Create ───────────────────────────────────────────────
        group.MapPost("/", async (string tenantSlug, HttpRequest request, IDocumentService documents, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var actor = SubjectResolver.GetSubject(user);
                if (string.IsNullOrWhiteSpace(actor))
                    return Results.Unauthorized();

                if (!request.HasFormContentType)
                    return Results.BadRequest("A multipart/form-data request with a 'file' part is required.");

                var form = await request.ReadFormAsync(ct);
                var file = form.Files["file"];
                if (file is null || file.Length == 0)
                    return Results.BadRequest("A non-empty 'file' part is required.");

                var documentType = form["documentType"].ToString();
                if (string.IsNullOrWhiteSpace(documentType))
                    return Results.BadRequest("A 'documentType' field is required.");

                var isVersioned = bool.TryParse(form["isVersioned"], out var v) && v;
                Guid? link = Guid.TryParse(form["businessObjectLink"], out var g) ? g : null;

                await using var stream = file.OpenReadStream();
                var document = await documents.CreateAsync(documentType, stream, actor, link, isVersioned, ct);

                return Results.Created(
                    $"/api/documents/t/{tenantSlug}/{document.DocumentUniqueId:N}",
                    ToResponse(document));
            })
            .RequireAuthorization(WritePolicy)
            .DisableAntiforgery()
            .WithName("CreateDocument");

        // ── Replace content ──────────────────────────────────────
        group.MapPut("/{id:guid}/content", async (string tenantSlug, Guid id, HttpRequest request, IDocumentService documents, IDocumentAccessPolicy policy, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (_, failure) = await AuthorizeAsync(documents, policy, tenantSlug, user, DocumentAction.Write, id, ct);
                if (failure is not null)
                    return failure;

                var actor = SubjectResolver.GetSubject(user)!;

                if (!request.HasFormContentType)
                    return Results.BadRequest("A multipart/form-data request with a 'file' part is required.");

                var form = await request.ReadFormAsync(ct);
                var file = form.Files["file"];
                if (file is null || file.Length == 0)
                    return Results.BadRequest("A non-empty 'file' part is required.");

                await using var stream = file.OpenReadStream();
                var document = await documents.UpdateContentAsync(id, stream, actor, ct);
                return Results.Ok(ToResponse(document));
            })
            .RequireAuthorization(WritePolicy)
            .DisableAntiforgery()
            .WithName("ReplaceDocumentContent");

        // ── Metadata ─────────────────────────────────────────────
        group.MapGet("/{id:guid}", async (string tenantSlug, Guid id, IDocumentService documents, IDocumentAccessPolicy policy, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (document, failure) = await AuthorizeAsync(documents, policy, tenantSlug, user, DocumentAction.Read, id, ct);
                return failure ?? Results.Ok(ToResponse(document!));
            })
            .RequireAuthorization(ReadPolicy)
            .WithName("GetDocument");

        // ── List (admin / maintenance) ───────────────────────────
        // Lists the tenant's documents, filtered through the access policy. This is NOT the business
        // listing path: « all documents of this client », « quotes to validate », etc. are answered from
        // the developer's own projection (built from the DocumentIndexed event), never from the GED.
        group.MapGet("/", async (string tenantSlug, IDocumentService documents, IDocumentAccessPolicy policy, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var actor = SubjectResolver.GetSubject(user);
                if (string.IsNullOrWhiteSpace(actor))
                    return Results.Unauthorized();

                var items = await documents.ListAsync(ct);
                var allowed = new List<DocumentResponse>();
                foreach (var document in items)
                {
                    if (await policy.IsAllowedAsync(new DocumentAccessRequest(tenantSlug, actor, DocumentAction.Read, document), ct))
                        allowed.Add(ToResponse(document));
                }

                return Results.Ok(allowed);
            })
            .RequireAuthorization(ReadPolicy)
            .WithName("ListDocuments");

        // ── Current content (pre-signed URL or streamed) ─────────
        group.MapGet("/{id:guid}/content", async (string tenantSlug, Guid id, IDocumentService documents, IDocumentAccessPolicy policy, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (_, failure) = await AuthorizeAsync(documents, policy, tenantSlug, user, DocumentAction.Read, id, ct);
                if (failure is not null)
                    return failure;

                var url = await documents.TryGetContentUrlAsync(id, DownloadUrlValidity, ct);
                if (url is not null)
                    return Results.Redirect(url.ToString());

                var stream = await documents.OpenContentAsync(id, ct);
                return Results.Stream(stream, "application/octet-stream");
            })
            .RequireAuthorization(ReadPolicy)
            .WithName("DownloadDocumentContent");

        // ── Version history ──────────────────────────────────────
        group.MapGet("/{id:guid}/versions", async (string tenantSlug, Guid id, IDocumentService documents, IDocumentAccessPolicy policy, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (_, failure) = await AuthorizeAsync(documents, policy, tenantSlug, user, DocumentAction.Read, id, ct);
                if (failure is not null)
                    return failure;

                var versions = await documents.GetVersionsAsync(id, ct);
                return Results.Ok(versions.Select(ToVersionResponse));
            })
            .RequireAuthorization(ReadPolicy)
            .WithName("ListDocumentVersions");

        // ── Specific version content (streamed) ──────────────────
        group.MapGet("/{id:guid}/versions/{version:int}/content", async (string tenantSlug, Guid id, int version, IDocumentService documents, IDocumentAccessPolicy policy, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (_, failure) = await AuthorizeAsync(documents, policy, tenantSlug, user, DocumentAction.Read, id, ct);
                if (failure is not null)
                    return failure;

                var stream = await documents.OpenVersionContentAsync(id, version, ct);
                return Results.Stream(stream, "application/octet-stream");
            })
            .RequireAuthorization(ReadPolicy)
            .WithName("DownloadDocumentVersionContent");

        // ── Trash ────────────────────────────────────────────────
        group.MapDelete("/{id:guid}", async (string tenantSlug, Guid id, IDocumentService documents, IDocumentAccessPolicy policy, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (_, failure) = await AuthorizeAsync(documents, policy, tenantSlug, user, DocumentAction.Delete, id, ct);
                if (failure is not null)
                    return failure;

                var actor = SubjectResolver.GetSubject(user)!;
                await documents.TrashAsync(id, actor, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(WritePolicy)
            .WithName("TrashDocument");

        return app;
    }

    // Resolves the document and runs the per-document access policy. Returns the authorized document,
    // or a failure result: 401 without a subject, 404 when the document is missing OR access is denied
    // (a denial is indistinguishable from absence, so existence is never disclosed).
    private static async Task<(Document? Document, IResult? Failure)> AuthorizeAsync(
        IDocumentService documents, IDocumentAccessPolicy policy, string tenantSlug,
        ClaimsPrincipal user, DocumentAction action, Guid id, CancellationToken ct)
    {
        var actor = SubjectResolver.GetSubject(user);
        if (string.IsNullOrWhiteSpace(actor))
            return (null, Results.Unauthorized());

        var document = await documents.GetAsync(id, ct);
        if (document is null)
            return (null, Results.NotFound());

        if (!await policy.IsAllowedAsync(new DocumentAccessRequest(tenantSlug, actor, action, document), ct))
            return (null, Results.NotFound());

        return (document, null);
    }

    private static DocumentResponse ToResponse(Document d) => new(
        DocumentId: d.DocumentUniqueId.ToString("N"),
        DocumentType: d.DocumentType,
        ContentHash: d.ContentHash,
        BusinessObjectLink: d.BusinessObjectLink?.ToString("N"),
        IsVersioned: d.IsVersioned,
        Status: d.Status,
        CreatedAt: d.CreatedAt,
        ModifiedAt: d.ModifiedAt);

    private static DocumentVersionResponse ToVersionResponse(DocumentVersion v) => new(
        VersionNumber: v.VersionNumber,
        CreatedAt: v.CreatedAt,
        CreatedBy: v.CreatedBy);
}

/// <summary>API projection of a managed document — system metadata only, never the storage key.</summary>
public sealed record DocumentResponse(
    string DocumentId,
    string DocumentType,
    string ContentHash,
    string? BusinessObjectLink,
    bool IsVersioned,
    DocumentStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ModifiedAt);

/// <summary>API projection of a document revision.</summary>
public sealed record DocumentVersionResponse(
    int VersionNumber,
    DateTimeOffset CreatedAt,
    string CreatedBy);
