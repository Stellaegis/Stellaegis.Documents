using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Stellaegis.Primitives;

namespace Stellaegis.Documents.AspNetCore;

/// <summary>
/// Maps the document API: <see cref="IDocumentService"/> exposed over HTTP, for browsers and for the
/// client in <c>Stellaegis.Documents.Client</c>.
/// </summary>
/// <remarks>
/// <para>Endpoints, relative to the builder they are mapped on:</para>
/// <list type="bullet">
///   <item><c>POST documents</c> — create a document from a <c>multipart/form-data</c> request: a
///   <c>documentType</c> field, an optional <c>isVersioned</c> field, then a <c>file</c> part</item>
///   <item><c>GET documents?continuationToken=…</c> — one page of the holder's active documents</item>
///   <item><c>GET documents/{id}</c> — a document's metadata</item>
///   <item><c>PUT documents/{id}/content</c> — replace its content (same multipart form, <c>file</c> only)</item>
///   <item><c>GET documents/{id}/content</c> — its content: a redirect to a pre-signed URL when the blob store
///   offers one, the content streamed otherwise</item>
///   <item>a streamed content carries its SHA-256 as <c>Repr-Digest</c> (RFC 9530) and as a strong <c>ETag</c>,
///   and answers <c>Range</c> requests, so an interrupted download resumes; the <c>file</c> part of an upload
///   may carry a <c>Content-Digest</c>, and content that does not match it is refused with <c>400</c></item>
///   <item>an upload streams from the request to the blob store, never buffered in memory nor on disk: the
///   fields must therefore come before the <c>file</c> part, which is read last. The host's request size
///   limit applies (30 MB by default under Kestrel): raise it on the group for larger documents</item>
///   <item><c>GET documents/{id}/versions</c> and <c>GET documents/{id}/versions/{n}/content</c> — its history</item>
///   <item><c>DELETE documents/{id}</c> — move it to the trash</item>
/// </list>
/// <para>
/// The host decides who the holder of each request is: <c>resolveHolder</c> reads it from the request — a
/// route value, a claim, a constant — once the host has checked the caller's rights on it, since a holder
/// read from the URL is user input. A <see langword="null"/> holder answers <c>404</c>. The actor recorded on
/// each change is the caller's <c>sub</c> claim (or name identifier); without one, the call answers
/// <c>401</c>.
/// </para>
/// <para>
/// Each document access then goes through the registered <see cref="IDocumentAccessPolicy"/>, or through
/// <see cref="AllowAllDocumentAccessPolicy"/> when none is, which is logged as a warning when the endpoints
/// are mapped. A denial answers <c>404</c>, like an absent document. A <see cref="UserFacingException"/>
/// answers a problem with the status of its <see cref="ErrorType"/>.
/// </para>
/// <para>
/// Everything else about the host — authentication schemes, the policies themselves, CORS, rate limiting,
/// forwarded headers, health checks — is the caller's to configure.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// builder.Services.AddStellaegisDocumentsPostgreSQL(builder.Configuration);
/// builder.Services.AddStellaegisFileSystemBlobStore(o => o.RootPath = "...");
/// builder.Services.AddAuthentication(...);
/// builder.Services.AddAuthorization(o => { o.AddPolicy("documents.read", ...); o.AddPolicy("documents.write", ...); });
///
/// var app = builder.Build();
/// app.UseAuthentication();
/// app.UseAuthorization();
/// app.MapGroup("/h/{holder}").MapDocumentsEndpoints(
///     http => http.GetRouteValue("holder") as string,
///     readPolicy: "documents.read",
///     writePolicy: "documents.write");
/// </code>
/// </example>
public static class EndpointRouteBuilderExtensions
{
    private static readonly TimeSpan DownloadUrlValidity = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maps the document endpoints. Reads require <paramref name="readPolicy"/>, changes require
    /// <paramref name="writePolicy"/>.
    /// </summary>
    /// <param name="endpoints">The builder to map the endpoints on — the application, or a route group.</param>
    /// <param name="resolveHolder">Gives the holder of a request, or <see langword="null"/> to answer 404.</param>
    /// <param name="readPolicy">Name of the authorization policy every read must satisfy.</param>
    /// <param name="writePolicy">Name of the authorization policy every change must satisfy.</param>
    /// <returns>The group holding the endpoints, for further conventions.</returns>
    public static RouteGroupBuilder MapDocumentsEndpoints(
        this IEndpointRouteBuilder endpoints,
        Func<HttpContext, string?> resolveHolder,
        string readPolicy,
        string writePolicy)
    {
        Guard.NotNull(endpoints);
        Guard.NotNull(resolveHolder);
        Guard.NotWhiteSpace(readPolicy);
        Guard.NotWhiteSpace(writePolicy);

        WarnWhenAllowingAll(endpoints.ServiceProvider);

        var group = endpoints.MapGroup("documents");
        group.AddEndpointFilter(AnswerUserFacingErrors);

        group.MapPost("/", async (HttpContext http, IDocumentService documents, CancellationToken ct) =>
            {
                if (Caller(http, resolveHolder) is not { } caller)
                    return Refusal(http, resolveHolder);

                return await WithUploadAsync(http, async (fields, upload) =>
                {
                    if (string.IsNullOrWhiteSpace(fields.DocumentType))
                        return BadRequest("A 'documentType' field is required, before the 'file' part.");

                    var document = await documents.CreateAsync(caller.Holder, fields.DocumentType, upload, caller.Actor, fields.IsVersioned, ct);
                    return Results.Created($"{http.Request.PathBase}{http.Request.Path.Value?.TrimEnd('/')}/{document.DocumentUniqueId:N}", DocumentResponse.From(document));
                }, ct);
            })
            .RequireAuthorization(writePolicy)
            .DisableAntiforgery()
            .WithName("CreateDocument")
            .Produces<DocumentResponse>(StatusCodes.Status201Created);

        group.MapGet("/", async (HttpContext http, string? continuationToken, IDocumentService documents, CancellationToken ct) =>
            {
                if (Caller(http, resolveHolder) is not { } caller)
                    return Refusal(http, resolveHolder);

                // A page filtered by the access policy may come back shorter than the page size: its token
                // still positions the next one.
                var page = await documents.ListAsync(caller.Holder, continuationToken, ct);
                var policy = Policy(http);
                var allowed = new List<DocumentResponse>();
                foreach (var document in page.Documents)
                {
                    if (await policy.IsAllowedAsync(new DocumentAccessRequest(caller.Holder, caller.Actor, DocumentAction.Read, document), ct))
                        allowed.Add(DocumentResponse.From(document));
                }

                return Results.Ok(new DocumentPageResponse(allowed, page.ContinuationToken));
            })
            .RequireAuthorization(readPolicy)
            .WithName("ListDocuments")
            .Produces<DocumentPageResponse>();

        group.MapGet("/{id:guid}", async (HttpContext http, Guid id, IDocumentService documents, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, resolveHolder, documents, DocumentAction.Read, id, ct);
                return access.Failure ?? Results.Ok(DocumentResponse.From(access.Document!));
            })
            .RequireAuthorization(readPolicy)
            .WithName("GetDocument")
            .Produces<DocumentResponse>();

        group.MapPut("/{id:guid}/content", async (HttpContext http, Guid id, IDocumentService documents, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, resolveHolder, documents, DocumentAction.Write, id, ct);
                if (access.Failure is not null)
                    return access.Failure;

                return await WithUploadAsync(http, async (_, upload) =>
                {
                    var document = await documents.UpdateContentAsync(access.Holder, id, upload, access.Actor, ct);
                    return Results.Ok(DocumentResponse.From(document));
                }, ct);
            })
            .RequireAuthorization(writePolicy)
            .DisableAntiforgery()
            .WithName("ReplaceDocumentContent")
            .Produces<DocumentResponse>();

        group.MapGet("/{id:guid}/content", async (HttpContext http, Guid id, IDocumentService documents, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, resolveHolder, documents, DocumentAction.Read, id, ct);
                if (access.Failure is not null)
                    return access.Failure;

                var url = await documents.TryGetContentUrlAsync(access.Holder, id, DownloadUrlValidity, ct);
                if (url is not null)
                    return Results.Redirect(url.ToString());

                var document = access.Document!;
                var stream = await documents.OpenContentAsync(access.Holder, id, ct);
                return Content(http, stream, document.ContentType, document.FileName, document.ContentHash);
            })
            .RequireAuthorization(readPolicy)
            .WithName("GetDocumentContent");

        group.MapGet("/{id:guid}/versions", async (HttpContext http, Guid id, IDocumentService documents, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, resolveHolder, documents, DocumentAction.Read, id, ct);
                if (access.Failure is not null)
                    return access.Failure;

                var versions = await documents.GetVersionsAsync(access.Holder, id, ct);
                return Results.Ok(versions.Select(DocumentVersionResponse.From).ToList());
            })
            .RequireAuthorization(readPolicy)
            .WithName("ListDocumentVersions")
            .Produces<IReadOnlyList<DocumentVersionResponse>>();

        group.MapGet("/{id:guid}/versions/{version:int}/content", async (HttpContext http, Guid id, int version, IDocumentService documents, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, resolveHolder, documents, DocumentAction.Read, id, ct);
                if (access.Failure is not null)
                    return access.Failure;

                var revision = (await documents.GetVersionsAsync(access.Holder, id, ct)).FirstOrDefault(v => v.VersionNumber == version);
                if (revision is null)
                    return Results.NotFound();

                var stream = await documents.OpenVersionContentAsync(access.Holder, id, version, ct);
                return Content(http, stream, revision.ContentType, revision.FileName, revision.ContentHash);
            })
            .RequireAuthorization(readPolicy)
            .WithName("GetDocumentVersionContent");

        group.MapDelete("/{id:guid}", async (HttpContext http, Guid id, IDocumentService documents, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, resolveHolder, documents, DocumentAction.Delete, id, ct);
                if (access.Failure is not null)
                    return access.Failure;

                await documents.TrashAsync(access.Holder, id, access.Actor, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(writePolicy)
            .WithName("TrashDocument")
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }

    /// <summary>
    /// Maps <c>DELETE documents</c>, which purges every document of the request's holder for good
    /// (<see cref="IDocumentService.PurgeHolderAsync"/>) and returns how many were deleted. Mapped apart from
    /// <see cref="MapDocumentsEndpoints"/> so that a host exposes it only by choice, under a policy of its own.
    /// </summary>
    /// <param name="endpoints">The builder to map the endpoint on — the application, or a route group.</param>
    /// <param name="resolveHolder">Gives the holder of a request, or <see langword="null"/> to answer 404.</param>
    /// <param name="purgePolicy">Name of the authorization policy a purge must satisfy.</param>
    /// <returns>The endpoint, for further conventions.</returns>
    public static RouteHandlerBuilder MapDocumentsPurgeEndpoint(
        this IEndpointRouteBuilder endpoints,
        Func<HttpContext, string?> resolveHolder,
        string purgePolicy)
    {
        Guard.NotNull(endpoints);
        Guard.NotNull(resolveHolder);
        Guard.NotWhiteSpace(purgePolicy);

        return endpoints.MapDelete("documents", async (HttpContext http, IDocumentService documents, CancellationToken ct) =>
            {
                if (Caller(http, resolveHolder) is not { } caller)
                    return Refusal(http, resolveHolder);

                var purged = await documents.PurgeHolderAsync(caller.Holder, ct);
                return Results.Ok(new { purged });
            })
            .RequireAuthorization(purgePolicy)
            .AddEndpointFilter(AnswerUserFacingErrors)
            .WithName("PurgeHolderDocuments");
    }

    private static async Task<Access> AuthorizeAsync(
        HttpContext http, Func<HttpContext, string?> resolveHolder, IDocumentService documents,
        DocumentAction action, Guid id, CancellationToken ct)
    {
        if (Caller(http, resolveHolder) is not { } caller)
            return Access.Refused(Refusal(http, resolveHolder));

        var document = await documents.GetAsync(caller.Holder, id, ct);
        if (document is null)
            return Access.Refused(Results.NotFound());

        // A denial is indistinguishable from an absence, so a caller cannot probe a document's existence.
        var allowed = await Policy(http).IsAllowedAsync(new DocumentAccessRequest(caller.Holder, caller.Actor, action, document), ct);
        return allowed ? new Access(caller.Holder, caller.Actor, document, null) : Access.Refused(Results.NotFound());
    }

    private static (string Holder, string Actor)? Caller(HttpContext http, Func<HttpContext, string?> resolveHolder)
    {
        var holder = resolveHolder(http);
        var actor = Subject(http.User);
        return holder is null || string.IsNullOrWhiteSpace(actor) ? null : (holder, actor);
    }

    // Caller is null for one of two reasons: no subject (401) or no holder for this request (404).
    private static IResult Refusal(HttpContext http, Func<HttpContext, string?> resolveHolder)
        => string.IsNullOrWhiteSpace(Subject(http.User)) ? Results.Unauthorized() : Results.NotFound();

    private static string? Subject(ClaimsPrincipal user)
        => user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private static IDocumentAccessPolicy Policy(HttpContext http)
        => http.RequestServices.GetService<IDocumentAccessPolicy>() ?? AllowAllPolicy;

    private static readonly AllowAllDocumentAccessPolicy AllowAllPolicy = new();

    private const int MaxFieldLength = 1024;

    private sealed record UploadFields(string? DocumentType, bool IsVersioned);

    // Reads the multipart request as it arrives: the fields, then the 'file' part, whose body is handed to
    // the service as it streams in. Nothing of the content is buffered, in memory or on disk.
    private static async Task<IResult> WithUploadAsync(
        HttpContext http, Func<UploadFields, DocumentUpload, Task<IResult>> handle, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is not { Length: > 0 } boundary)
            return BadRequest("A multipart/form-data request with a 'file' part is required.");

        var reader = new MultipartReader(boundary, http.Request.Body);
        string? documentType = null;
        var isVersioned = false;

        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                continue;

            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            if (disposition.IsFormDisposition())
            {
                var value = await ReadFieldAsync(section, ct);
                if (value is null)
                    return BadRequest($"The '{name}' field is longer than {MaxFieldLength} characters.");

                if (name == "documentType")
                    documentType = value;
                else if (name == "isVersioned")
                    isVersioned = bool.TryParse(value, out var versioned) && versioned;
                continue;
            }

            if (!disposition.IsFileDisposition() || name != "file")
                continue;

            // Absent: nothing to check. Present without a readable SHA-256 entry: refused, rather than stored
            // unchecked.
            string? expectedHash = null;
            if (section.Headers?.TryGetValue("Content-Digest", out var digest) == true)
            {
                if (!ContentDigest.TryReadSha256(digest.ToString(), out var hash))
                    return BadRequest("The 'Content-Digest' of the 'file' part is malformed.");
                expectedHash = hash;
            }

            var fileName = disposition.FileNameStar.HasValue ? disposition.FileNameStar.Value : HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
            var upload = new DocumentUpload
            {
                Content = section.Body,
                FileName = Path.GetFileName(fileName ?? string.Empty),
                ContentType = string.IsNullOrWhiteSpace(section.ContentType) ? "application/octet-stream" : section.ContentType,
                ExpectedContentHash = expectedHash,
            };
            return await handle(new UploadFields(documentType, isVersioned), upload);
        }

        return BadRequest("A 'file' part is required.");
    }

    // A field is a short value: reading stops past MaxFieldLength rather than buffer whatever is sent.
    private static async Task<string?> ReadFieldAsync(MultipartSection section, CancellationToken ct)
    {
        using var reader = new StreamReader(section.Body);
        var buffer = new char[MaxFieldLength + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await reader.ReadAsync(buffer.AsMemory(length), ct)) > 0)
            length += read;

        return length > MaxFieldLength ? null : new string(buffer, 0, length);
    }

    private static IResult BadRequest(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);

    // Repr-Digest describes the whole content, so it stays true on a partial (Range) answer, where a
    // Content-Digest would describe only the bytes sent.
    private static IResult Content(HttpContext http, Stream stream, string contentType, string fileName, string contentHash)
    {
        http.Response.Headers["Repr-Digest"] = ContentDigest.Format(contentHash);
        return Results.Stream(stream, contentType, fileName,
            entityTag: new EntityTagHeaderValue($"\"{contentHash}\""), enableRangeProcessing: true);
    }

    private static async ValueTask<object?> AnswerUserFacingErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (UserFacingException e)
        {
            return Results.Problem(e.Message, statusCode: StatusFor(e.Type));
        }
        catch (ArgumentException e)
        {
            // The service guards its arguments: what reaches it from a request is the caller's input.
            return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    private static void WarnWhenAllowingAll(IServiceProvider services)
    {
        if (services.GetService<IServiceProviderIsService>()?.IsService(typeof(IDocumentAccessPolicy)) == true)
            return;

        services.GetService<ILoggerFactory>()?.CreateLogger(typeof(EndpointRouteBuilderExtensions).FullName!).LogWarning(
            "No IDocumentAccessPolicy is registered: every caller who satisfies the read or write policy can " +
            "access every document of the holder. Register an IDocumentAccessPolicy for per-document authorization.");
    }

    private sealed record Access(string Holder, string Actor, Document? Document, IResult? Failure)
    {
        public static Access Refused(IResult failure) => new(string.Empty, string.Empty, null, failure);
    }
}
