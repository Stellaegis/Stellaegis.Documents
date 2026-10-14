using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Stellaegis.Primitives;

namespace Stellaegis.Documents.Client;

/// <summary>
/// <see cref="IDocumentService"/> over the document API mapped by <c>Stellaegis.Documents.AspNetCore</c>, so
/// that a consumer works the same in process or remotely.
/// </summary>
/// <remarks>
/// <para>
/// The server records the authenticated caller as the actor of a change: the <c>actor</c> argument is not
/// sent. Authenticating the requests is the caller's concern, through the HTTP client builder.
/// </para>
/// <para>
/// Answers map to the exceptions of the in-process service: 404 to <see cref="DocumentNotFoundException"/>,
/// 400, 401, 403 and 409 to a <see cref="UserFacingException"/> of the matching <see cref="ErrorType"/>.
/// </para>
/// <para>
/// Integrity, end to end: an upload carries the SHA-256 of its content as <c>Content-Digest</c> — the one the
/// caller set in <see cref="DocumentUpload.ExpectedContentHash"/>, or one computed here when the content can
/// be read twice (a seekable stream) — and the server refuses content that does not match. A download is
/// checked against the server's <c>Repr-Digest</c> as it is read: its end throws
/// <see cref="InvalidDataException"/> when the content was damaged on its way.
/// </para>
/// </remarks>
internal sealed class HttpDocumentService(HttpClient http, DocumentsHolderPath holderPath) : IDocumentService
{
    private readonly HttpClient _http = Guard.NotNull(http);
    private readonly DocumentsHolderPath _holderPath = Guard.NotNull(holderPath);

    public async Task<Document> CreateAsync(string holder, string documentType, DocumentUpload upload, string actor, bool isVersioned = false, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(holder);
        Guard.NotWhiteSpace(documentType);
        Guard.NotNull(upload);

        // The server reads the fields before the file part, which streams last.
        using var form = new MultipartFormDataContent
        {
            { new StringContent(documentType), "documentType" },
            { new StringContent(isVersioned ? "true" : "false"), "isVersioned" },
        };
        AddFile(form, upload);

        using var response = await _http.PostAsync(Url(holder, string.Empty), form, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, null, cancellationToken).ConfigureAwait(false);
        return ToDocument(holder, await ReadAsync<DocumentResponse>(response, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Document> UpdateContentAsync(string holder, Guid documentUniqueId, DocumentUpload upload, string actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(holder);
        Guard.NotNull(upload);

        using var form = new MultipartFormDataContent();
        AddFile(form, upload);
        using var response = await _http.PutAsync(Url(holder, $"/{documentUniqueId:N}/content"), form, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, documentUniqueId, cancellationToken).ConfigureAwait(false);
        return ToDocument(holder, await ReadAsync<DocumentResponse>(response, cancellationToken).ConfigureAwait(false));
    }

    public Task<Stream> OpenContentAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default)
        => OpenAsync(Url(Guard.NotNull(holder), $"/{documentUniqueId:N}/content"), documentUniqueId, cancellationToken);

    public Task<Stream> OpenVersionContentAsync(string holder, Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default)
        => OpenAsync(Url(Guard.NotNull(holder), $"/{documentUniqueId:N}/versions/{versionNumber}/content"), documentUniqueId, cancellationToken);

    public async Task<Document?> GetAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(holder);

        using var response = await _http.GetAsync(Url(holder, $"/{documentUniqueId:N}"), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, documentUniqueId, cancellationToken).ConfigureAwait(false);
        return ToDocument(holder, await ReadAsync<DocumentResponse>(response, cancellationToken).ConfigureAwait(false));
    }

    public async Task<DocumentPage> ListAsync(string holder, string? continuationToken = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(holder);

        var query = continuationToken is null ? string.Empty : "?continuationToken=" + Uri.EscapeDataString(continuationToken);
        using var response = await _http.GetAsync(Url(holder, query), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, null, cancellationToken).ConfigureAwait(false);

        var page = await ReadAsync<DocumentPageResponse>(response, cancellationToken).ConfigureAwait(false);
        return new DocumentPage(page.Documents.Select(d => ToDocument(holder, d)).ToList(), page.ContinuationToken);
    }

    public async Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(string holder, Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(holder);

        using var response = await _http.GetAsync(Url(holder, $"/{documentUniqueId:N}/versions"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, documentUniqueId, cancellationToken).ConfigureAwait(false);

        var versions = await ReadAsync<List<DocumentVersionResponse>>(response, cancellationToken).ConfigureAwait(false);
        return versions.Select(v => new DocumentVersion
        {
            DocumentUniqueId = documentUniqueId,
            VersionNumber = v.VersionNumber,
            FileName = v.FileName,
            ContentType = v.ContentType,
            ContentLength = v.ContentLength,
            ContentHash = v.ContentHash,
            CreatedAt = v.CreatedAt,
            CreatedBy = v.CreatedBy,
        }).ToList();
    }

    public async Task TrashAsync(string holder, Guid documentUniqueId, string actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(holder);

        using var response = await _http.DeleteAsync(Url(holder, $"/{documentUniqueId:N}"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, documentUniqueId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PurgeHolderAsync(string holder, CancellationToken cancellationToken = default)
    {
        Guard.NotWhiteSpace(holder);

        using var response = await _http.DeleteAsync(Url(holder, string.Empty), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, null, cancellationToken).ConfigureAwait(false);
        return (await ReadAsync<PurgeResponse>(response, cancellationToken).ConfigureAwait(false)).Purged;
    }

    private async Task<Stream> OpenAsync(string url, Guid documentUniqueId, CancellationToken cancellationToken)
    {
        // Headers only: the content streams to the caller, whose stream owns the response.
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, documentUniqueId, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (ContentDigest.TryReadSha256(Header(response, "Repr-Digest"), out var expected))
                content = new DigestCheckingStream(content, expected);

            return new ResponseContentStream(response, content);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private string Url(string holder, string rest) => $"{_holderPath.For(holder)}documents{rest}";

    private static void AddFile(MultipartFormDataContent form, DocumentUpload upload)
    {
        var content = Guard.NotNull(upload.Content);
        var hash = upload.ExpectedContentHash ?? HashIfSeekable(content);

        var file = new StreamContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(upload.ContentType);
        if (hash is not null)
            file.Headers.Add("Content-Digest", ContentDigest.Format(hash));

        form.Add(file, "file", upload.FileName);
    }

    // A forward-only stream cannot be hashed before it is sent without being held in memory: it goes
    // unchecked, unless the caller supplied its digest.
    private static string? HashIfSeekable(Stream content)
    {
        if (!content.CanSeek)
            return null;

        var start = content.Position;
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        content.Position = start;
        return hash;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : null;

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        => await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
           ?? throw new InvalidOperationException($"The document API answered {(int)response.StatusCode} without a body.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, Guid? documentUniqueId, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode == HttpStatusCode.NotFound && documentUniqueId is { } id)
            throw new DocumentNotFoundException(id);

        ErrorType? type = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => ErrorType.Validation,
            HttpStatusCode.Unauthorized => ErrorType.Unauthorized,
            HttpStatusCode.Forbidden => ErrorType.Forbidden,
            HttpStatusCode.NotFound => ErrorType.NotFound,
            HttpStatusCode.Conflict => ErrorType.Conflict,
            HttpStatusCode.PreconditionFailed => ErrorType.PreconditionFailed,
            _ => null,
        };
        if (type is null)
            response.EnsureSuccessStatusCode();

        throw new UserFacingException(await DetailAsync(response, cancellationToken).ConfigureAwait(false), type!.Value);
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = $"The document API answered {(int)response.StatusCode}.";
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return problem.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text
                ? text
                : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static Document ToDocument(string holder, DocumentResponse response) => new()
    {
        DocumentUniqueId = response.DocumentUniqueId,
        Holder = holder,
        DocumentType = response.DocumentType,
        VersionNumber = response.VersionNumber,
        ContentHash = response.ContentHash,
        FileName = response.FileName,
        ContentType = response.ContentType,
        ContentLength = response.ContentLength,
        Status = response.Status,
        IsVersioned = response.IsVersioned,
        CreatedAt = response.CreatedAt,
        CreatedBy = response.CreatedBy,
        ModifiedAt = response.ModifiedAt,
        ModifiedBy = response.ModifiedBy,
    };

    private sealed record PurgeResponse(int Purged);
}
