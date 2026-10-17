using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;

namespace Stellaegis.Documents.AspNetCore;

/// <summary>
/// Maps the document API: <see cref="IDocumentService"/> exposed over HTTP, for browsers and for the
/// client in <c>Stellaegis.Documents.Client</c>.
/// </summary>
/// <remarks>
/// <para>Endpoints, relative to the builder they are mapped on, each labelled with its operation from
/// <see cref="DocumentOperations"/>:</para>
/// <list type="bullet">
///   <item><c>POST documents</c> (<c>create</c>) — create a document from a <c>multipart/form-data</c> request:
///   a <c>documentType</c> field, an optional <c>isVersioned</c> field, then a <c>file</c> part</item>
///   <item><c>GET documents?continuationToken=…</c> (<c>list</c>) — one page of the active documents of the space</item>
///   <item><c>GET documents/{id}</c> (<c>read</c>) — a document's metadata</item>
///   <item><c>PUT documents/{id}/content</c> (<c>replace-content</c>) — replace its content (same multipart
///   form, <c>file</c> only)</item>
///   <item><c>GET documents/{id}/content</c> (<c>read-content</c>) — its content, streamed</item>
///   <item><c>GET documents/{id}/versions</c> (<c>list-versions</c>) and
///   <c>GET documents/{id}/versions/{n}/content</c> (<c>read-version-content</c>) — its history</item>
///   <item><c>DELETE documents/{id}</c> (<c>trash</c>) — move it to the trash</item>
///   <item>a streamed content carries its SHA-256 as <c>Repr-Digest</c> (RFC 9530) and as a strong <c>ETag</c>,
///   and answers <c>Range</c> requests, so an interrupted download resumes; the <c>file</c> part of an upload
///   may carry a <c>Content-Digest</c>, and content that does not match it is refused with <c>400</c></item>
///   <item>an upload streams from the request to the blob store, never buffered in memory nor on disk: the
///   fields must therefore come before the <c>file</c> part, which is read last. The host's request size
///   limit applies (30 MB by default under Kestrel): raise it on the group for larger documents</item>
/// </list>
/// <para>
/// The module attaches no security. The host requires its policies on the returned group, one per
/// operation — the operation's name is the key, its kind only follows the HTTP verb. It answers
/// <c>AuthorizeResourceAsync</c>, asked on each document a route loads, with an
/// <c>AuthorizationHandler&lt;Operation, Document&gt;</c>, or declares <c>AllowAnyResource()</c>; without
/// either, every document is refused. A refusal on a document answers <c>404</c>, like an absent one.
/// </para>
/// <para>
/// The space and the actor of each request come from the host's <c>IRequestContext</c>, registered with
/// <c>AddStellaegisRequestContext</c>: the compartment read from a route value, a claim or a constant, and the
/// actor recorded on each change. A compartment read from the URL is user input, which the host's security
/// checks. An unresolved space answers <c>404</c>. The module accepts the shared space: the host reaches it by
/// mapping the endpoints a second time on a group marked <c>InSharedSpace()</c>. Errors are answered as problem
/// details by the translation of <c>Stellaegis.Primitives.AspNetCore</c>.
/// </para>
/// <para>
/// The endpoints carry no route name, so the module can be mapped several times in one host. A host that wants
/// names — for links or an OpenAPI document — sets them from each route's operation.
/// </para>
/// <para>
/// The purge of a compartment is an act of administration on the compartment, not an operation within it:
/// <see cref="MapDocumentsAdminEndpoints"/> maps it apart, on a group the host secures for its administrators.
/// A host that does not map it has no purge.
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
/// builder.Services.AddStellaegisRequestContext(c => c.CompartmentFromRoute("org"));
/// builder.Services.AddAuthentication(...);
/// builder.Services.AddAuthorization(o => { o.AddPolicy("documents.read", ...); /* one per operation */ });
/// builder.Services.AddSingleton&lt;IAuthorizationHandler, MyDocumentAuthorizationHandler&gt;();
///
/// var app = builder.Build();
/// app.UseAuthentication();
/// app.UseAuthorization();
/// app.MapGroup("/api/orgs/{org}").MapDocumentsEndpoints()
///    .RequireAuthorizationPerOperation(op => op.ToString());   // "documents.read", "documents.trash"…
/// app.MapGroup("/api/shared").MapDocumentsEndpoints().InSharedSpace()
///    .RequireAuthorizationPerOperation(op => $"shared.{op}");
/// app.MapGroup("/api/admin").MapDocumentsAdminEndpoints()
///    .RequireAuthorization("platform-admin");
/// </code>
/// </example>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the document endpoints, each labelled with its operation from <see cref="DocumentOperations"/>,
    /// and none secured: the host attaches its security to the returned group.
    /// </summary>
    /// <param name="endpoints">The builder to map the endpoints on — the application, or a route group.</param>
    /// <returns>The group holding the endpoints, for the host's security and further conventions.</returns>
    public static RouteGroupBuilder MapDocumentsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Guard.NotNull(endpoints);

        var group = endpoints.MapGroup("documents")
            .BelongsTo<DocumentsModule>()
            .RequireRequestContext(acceptsSharedSpace: true);
        group.AddErrorTranslation();

        group.MapPost("/", async (HttpContext http, IRequestContext context, IDocumentService documents, CancellationToken ct) =>
            {
                if (context.Space is not { } space)
                    return Results.NotFound();

                return await WithUploadAsync(http, async (fields, upload) =>
                {
                    if (string.IsNullOrWhiteSpace(fields.DocumentType))
                        return BadRequest("A 'documentType' field is required, before the 'file' part.");

                    var document = await documents.CreateAsync(space, fields.DocumentType, upload, context.Actor, fields.IsVersioned, ct);
                    return Results.Created($"{http.Request.PathBase}{http.Request.Path.Value?.TrimEnd('/')}/{document.DocumentUniqueId:N}", DocumentResponse.From(document));
                }, ct);
            })
            .WithMetadata(DocumentOperations.Create)
            .DisableAntiforgery()
            .Produces<DocumentResponse>(StatusCodes.Status201Created);

        group.MapGet("/", async (HttpContext http, string? continuationToken, IRequestContext context, IDocumentService documents, CancellationToken ct) =>
            {
                if (context.Space is not { } space)
                    return Results.NotFound();

                // A page filtered by the host's authorization may come back shorter than the page size: its
                // token still positions the next one.
                var page = await documents.ListAsync(space, context.Actor, continuationToken, ct);
                var allowed = new List<DocumentResponse>();
                foreach (var document in page.Documents)
                {
                    if (await http.AuthorizeResourceAsync(document))
                        allowed.Add(DocumentResponse.From(document));
                }

                return Results.Ok(new DocumentPageResponse(allowed, page.ContinuationToken));
            })
            .WithMetadata(DocumentOperations.List)
            .Produces<DocumentPageResponse>();

        group.MapGet("/{id:guid}", async (HttpContext http, Guid id, IRequestContext context, IDocumentService documents, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, context, documents, id, ct);
                return access.Failure ?? Results.Ok(DocumentResponse.From(access.Document!));
            })
            .WithMetadata(DocumentOperations.Read)
            .Produces<DocumentResponse>();

        group.MapPut("/{id:guid}/content", async (HttpContext http, Guid id, IRequestContext context, IDocumentService documents, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, context, documents, id, ct);
                if (access.Failure is not null)
                    return access.Failure;

                return await WithUploadAsync(http, async (_, upload) =>
                {
                    var document = await documents.UpdateContentAsync(access.Space, id, upload, access.Actor, ct);
                    return Results.Ok(DocumentResponse.From(document));
                }, ct);
            })
            .WithMetadata(DocumentOperations.ReplaceContent)
            .DisableAntiforgery()
            .Produces<DocumentResponse>();

        group.MapGet("/{id:guid}/content", async (HttpContext http, Guid id, IRequestContext context, IDocumentService documents, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, context, documents, id, ct);
                if (access.Failure is not null)
                    return access.Failure;

                var document = access.Document!;
                var stream = await documents.OpenContentAsync(access.Space, id, access.Actor, ct);
                return Content(http, stream, document.ContentType, document.FileName, document.ContentHash);
            })
            .WithMetadata(DocumentOperations.ReadContent);

        group.MapGet("/{id:guid}/versions", async (HttpContext http, Guid id, IRequestContext context, IDocumentService documents, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, context, documents, id, ct);
                if (access.Failure is not null)
                    return access.Failure;

                var versions = await documents.GetVersionsAsync(access.Space, id, access.Actor, ct);
                return Results.Ok(versions.Select(DocumentVersionResponse.From).ToList());
            })
            .WithMetadata(DocumentOperations.ListVersions)
            .Produces<IReadOnlyList<DocumentVersionResponse>>();

        group.MapGet("/{id:guid}/versions/{version:int}/content", async (HttpContext http, Guid id, int version, IRequestContext context, IDocumentService documents, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, context, documents, id, ct);
                if (access.Failure is not null)
                    return access.Failure;

                var revision = (await documents.GetVersionsAsync(access.Space, id, access.Actor, ct)).FirstOrDefault(v => v.VersionNumber == version);
                if (revision is null)
                    return Results.NotFound();

                var stream = await documents.OpenVersionContentAsync(access.Space, id, version, access.Actor, ct);
                return Content(http, stream, revision.ContentType, revision.FileName, revision.ContentHash);
            })
            .WithMetadata(DocumentOperations.ReadVersionContent);

        group.MapDelete("/{id:guid}", async (HttpContext http, Guid id, IRequestContext context, IDocumentService documents, CancellationToken ct) =>
            {
                var access = await AuthorizeAsync(http, context, documents, id, ct);
                if (access.Failure is not null)
                    return access.Failure;

                await documents.TrashAsync(access.Space, id, access.Actor, ct);
                return Results.NoContent();
            })
            .WithMetadata(DocumentOperations.Trash)
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }

    /// <summary>
    /// Maps the administration endpoints of the module, labelled with their operation from
    /// <see cref="DocumentOperations"/> and none secured: the host attaches its security to the returned group.
    /// </summary>
    /// <remarks>
    /// <para>The compartment is the object of these endpoints, not their context: it is a segment of the path,
    /// and the request context gives only the actor.</para>
    /// <list type="bullet">
    ///   <item><c>DELETE documents/compartments/{compartment}</c> (<c>purge</c>) — delete every document of the
    ///   compartment for good (<see cref="IDocumentService.PurgeCompartmentAsync"/>), answering how many were
    ///   deleted. A replayed purge completes an interrupted one, and answers <c>0</c> once nothing is left.
    ///   <c>404</c> for a value that is not a compartment: the shared space has no purge</item>
    /// </list>
    /// </remarks>
    /// <param name="endpoints">The builder to map the endpoints on — the application, or a route group.</param>
    /// <returns>The group holding the endpoints, for the host's security and further conventions.</returns>
    public static RouteGroupBuilder MapDocumentsAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Guard.NotNull(endpoints);

        var group = endpoints.MapGroup("documents")
            .BelongsTo<DocumentsModule>()
            .RequireRequestContext();
        group.AddErrorTranslation();

        group.MapDelete("/compartments/{compartment}", async (string compartment, IRequestContext context, IDocumentService documents, CancellationToken ct) =>
            {
                if (!Compartment.TryParse(compartment, out var target))
                    return Results.NotFound();

                var purged = await documents.PurgeCompartmentAsync(target, context.Actor, ct);
                return Results.Ok(new { purged });
            })
            .WithMetadata(DocumentOperations.Purge);

        return group;
    }

    private static async Task<Access> AuthorizeAsync(
        HttpContext http, IRequestContext context, IDocumentService documents, Guid id, CancellationToken ct)
    {
        if (context.Space is not { } space)
            return Access.Refused(Results.NotFound());

        // A refusal is indistinguishable from an absence, so a caller cannot probe a document's existence.
        var document = await documents.GetAsync(space, id, context.Actor, ct);
        if (document is null || !await http.AuthorizeResourceAsync(document))
            return Access.Refused(Results.NotFound());

        return new Access(space, context.Actor, document, null);
    }

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

    private sealed record Access(Space Space, Actor Actor, Document? Document, IResult? Failure)
    {
        public static Access Refused(IResult failure) => new(null!, null!, null, failure);
    }
}
