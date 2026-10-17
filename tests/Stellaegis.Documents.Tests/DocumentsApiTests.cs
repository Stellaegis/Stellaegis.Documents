using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Stellaegis.Documents.AspNetCore;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;
using static Stellaegis.Documents.Tests.Content;

namespace Stellaegis.Documents.Tests;

/// <summary>The mapped endpoints as raw HTTP: what a browser or any other client meets.</summary>
public sealed class DocumentsApiTests(DocumentsHost host) : IClassFixture<DocumentsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MultipartFormDataContent Form(string text, string fileName = "file.txt", string mediaType = "text/plain",
        string? documentType = "invoice", bool versioned = false, string? digest = null)
    {
        var form = new MultipartFormDataContent();
        if (documentType is not null)
            form.Add(new StringContent(documentType), "documentType");
        if (versioned)
            form.Add(new StringContent("true"), "isVersioned");

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        if (digest is not null)
            file.Headers.Add("Content-Digest", digest);
        form.Add(file, "file", fileName);
        return form;
    }

    private async Task<(Compartment Compartment, DocumentResponse Document)> CreateAsync(HttpClient http, string text = "hello", string fileName = "file.txt", bool versioned = false)
    {
        var compartment = NewCompartment();
        var created = await http.PostAsync($"/api/t/{compartment}/documents", Form(text, fileName, versioned: versioned), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (compartment, (await created.Content.ReadFromJsonAsync<DocumentResponse>(Ct))!);
    }

    [Fact]
    public async Task Creating_answers_201_with_the_location_and_the_metadata()
    {
        var http = host.Http("alice");
        var compartment = NewCompartment();

        var created = await http.PostAsync($"/api/t/{compartment}/documents", Form("hello", "Facture été.txt"), Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var document = (await created.Content.ReadFromJsonAsync<DocumentResponse>(Ct))!;
        Assert.Equal(("Facture été.txt", 5L, 1, "alice"), (document.FileName, document.ContentLength, document.VersionNumber, document.CreatedBy));
        Assert.Equal($"/api/t/{compartment}/documents/{document.DocumentUniqueId:N}", created.Headers.Location?.ToString());
    }

    [Fact]
    public async Task A_download_carries_type_name_digest_and_etag()
    {
        var http = host.Http("alice");
        var (compartment, document) = await CreateAsync(http, "hello", "Facture été.txt");

        var content = await http.GetAsync($"/api/t/{compartment}/documents/{document.DocumentUniqueId}/content", Ct);

        Assert.Equal("hello", await content.Content.ReadAsStringAsync(Ct));
        Assert.Equal("text/plain", content.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Facture été.txt", content.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(ContentDigest.Format(Hash("hello")), Assert.Single(content.Headers.GetValues("Repr-Digest")));
        Assert.Equal($"\"{Hash("hello")}\"", content.Headers.ETag?.Tag);
        Assert.False(content.Headers.ETag!.IsWeak);
    }

    [Fact]
    public async Task A_range_request_resumes_a_download_and_an_unchanged_etag_answers_304()
    {
        var http = host.Http("alice");
        var (compartment, document) = await CreateAsync(http, "hello");
        var url = $"/api/t/{compartment}/documents/{document.DocumentUniqueId}/content";

        var ranged = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(1, 2) } };
        var partial = await http.SendAsync(ranged, Ct);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("el", await partial.Content.ReadAsStringAsync(Ct));

        var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{Hash("hello")}\""));
        Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(conditional, Ct)).StatusCode);
    }

    [Fact]
    public async Task An_upload_is_checked_against_its_content_digest()
    {
        var http = host.Http("alice");
        var compartment = NewCompartment();

        var damaged = await http.PostAsync($"/api/t/{compartment}/documents", Form("hellO", digest: ContentDigest.Format(Hash("hello"))), Ct);
        var intact = await http.PostAsync($"/api/t/{compartment}/documents", Form("hello", digest: ContentDigest.Format(Hash("hello"))), Ct);
        var malformed = await http.PostAsync($"/api/t/{compartment}/documents", Form("hello", digest: "sha-256=nonsense"), Ct);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.Created, HttpStatusCode.BadRequest), (damaged.StatusCode, intact.StatusCode, malformed.StatusCode));
    }

    [Fact]
    public async Task The_fields_come_before_the_file_part()
    {
        var http = host.Http("alice");
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("x"u8.ToArray()), "file", "x.txt" },
            { new StringContent("invoice"), "documentType" },
        };

        var refused = await http.PostAsync($"/api/t/{NewCompartment()}/documents", form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("before the 'file' part", await refused.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Refusals_answer_401_404_and_400()
    {
        var alice = host.Http("alice");
        var (compartment, document) = await CreateAsync(alice);
        var url = $"/api/t/{compartment}/documents/{document.DocumentUniqueId}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Http(null).GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/t/{NewCompartment()}/documents/{document.DocumentUniqueId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http("mallory").GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsync($"/api/t/{compartment}/documents", Form("x", documentType: null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsync("/api/t/NotACompartment/documents", Form("x"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync($"/api/t/{compartment}/documents?continuationToken=not*a*token", Ct)).StatusCode);
    }

    [Fact]
    public async Task Versions_are_listed_and_served_under_their_own_names()
    {
        var http = host.Http("alice");
        var (compartment, document) = await CreateAsync(http, "c1", "contrat.txt", versioned: true);
        var url = $"/api/t/{compartment}/documents/{document.DocumentUniqueId}";

        var replaced = await http.PutAsync($"{url}/content", Form("c2", "contrat-signé.pdf", "application/pdf", documentType: null), Ct);
        Assert.Equal(2, (await replaced.Content.ReadFromJsonAsync<DocumentResponse>(Ct))!.VersionNumber);

        var versions = (await http.GetFromJsonAsync<List<DocumentVersionResponse>>($"{url}/versions", Ct))!;
        Assert.Equal(["contrat.txt", "contrat-signé.pdf"], versions.Select(v => v.FileName));
        var first = await http.GetAsync($"{url}/versions/1/content", Ct);
        Assert.Equal(("c1", "contrat.txt"), (await first.Content.ReadAsStringAsync(Ct), first.Content.Headers.ContentDisposition?.FileNameStar));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"{url}/versions/9/content", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_list_pages_with_its_token_and_skips_trashed_documents()
    {
        var http = host.Http("alice");
        var compartment = NewCompartment();
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var created = await http.PostAsync($"/api/t/{compartment}/documents", Form($"n{i}"), Ct);
            ids.Add((await created.Content.ReadFromJsonAsync<DocumentResponse>(Ct))!.DocumentUniqueId);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/t/{compartment}/documents/{ids[0]}", Ct)).StatusCode);

        var first = (await http.GetFromJsonAsync<DocumentPageResponse>($"/api/t/{compartment}/documents", Ct))!;
        Assert.Equal(2, first.Documents.Count);
        Assert.NotNull(first.ContinuationToken);
        var second = (await http.GetFromJsonAsync<DocumentPageResponse>($"/api/t/{compartment}/documents?continuationToken={Uri.EscapeDataString(first.ContinuationToken)}", Ct))!;

        Assert.Equal(ids.Skip(1).Reverse(), first.Documents.Concat(second.Documents).Select(d => d.DocumentUniqueId));
    }

    [Fact]
    public async Task The_list_leaves_out_the_documents_the_host_refuses()
    {
        var (compartment, document) = await CreateAsync(host.Http("alice"));

        var seen = (await host.Http("bob").GetFromJsonAsync<DocumentPageResponse>($"/api/t/{compartment}/documents", Ct))!;
        var refused = (await host.Http("mallory").GetFromJsonAsync<DocumentPageResponse>($"/api/t/{compartment}/documents", Ct))!;

        Assert.Equal([document.DocumentUniqueId], seen.Documents.Select(d => d.DocumentUniqueId));
        Assert.Empty(refused.Documents);
    }

    [Fact]
    public void Every_route_carries_an_operation_of_its_own()
    {
        var operations = host.Endpoints
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/t/", StringComparison.Ordinal))
            .Select(e => e.GetOperation()).ToList();

        Assert.Equal(8, operations.Count);
        Assert.DoesNotContain(null, operations);
        Assert.Equal(operations.Count, operations.Distinct().Count());
        Assert.All(operations, op => Assert.Equal(DocumentOperations.Target, op!.Target));
    }

    [Fact]
    public void The_routes_carry_no_name_so_the_module_maps_twice()
    {
        Assert.Equal(17, host.Endpoints.Count);
        Assert.All(host.Endpoints, e => Assert.Null(e.Metadata.GetMetadata<IEndpointNameMetadata>()));
    }

    [Fact]
    public async Task The_shared_space_is_reached_only_through_its_marked_routes()
    {
        var http = host.Http("alice");
        var created = await http.PostAsync("/api/shared/documents", Form("logo", "logo.svg"), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var shared = (await created.Content.ReadFromJsonAsync<DocumentResponse>(Ct))!;
        var (compartment, owned) = await CreateAsync(http);

        Assert.Equal("logo", await http.GetStringAsync($"/api/shared/documents/{shared.DocumentUniqueId}/content", Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/t/{compartment}/documents/{shared.DocumentUniqueId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/shared/documents/{owned.DocumentUniqueId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task An_unresolved_space_and_an_unmapped_route_both_answer_404()
    {
        var http = host.Http("alice");

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/t/Not-A-Compartment/documents", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/elsewhere/documents", Ct)).StatusCode);
    }

    [Fact]
    public void The_purge_is_only_in_the_administration_mapping()
    {
        var purges = host.Endpoints.Where(e => e.GetOperation() == DocumentOperations.Purge).ToList();

        Assert.Equal(["/api/admin/documents/compartments/{compartment}"], purges.Select(e => e.RoutePattern.RawText));
    }

    [Fact]
    public async Task The_shared_space_has_no_purge()
    {
        var created = await host.Http("alice").PostAsync("/api/shared/documents", Form("logo"), Ct);
        var shared = (await created.Content.ReadFromJsonAsync<DocumentResponse>(Ct))!;

        Assert.Equal(HttpStatusCode.NotFound, (await host.Http("ops", "admin").DeleteAsync("/api/admin/documents/compartments/_shared", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Http("alice").GetAsync($"/api/shared/documents/{shared.DocumentUniqueId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_purge_requires_the_policy_of_the_administration_mapping()
    {
        var alice = host.Http("alice");
        var (compartment, _) = await CreateAsync(alice);

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/admin/documents/compartments/{compartment}", Ct)).StatusCode);
        var purge = await host.Http("ops", "admin").DeleteAsync($"/api/admin/documents/compartments/{compartment}", Ct);

        Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
        Assert.Contains("\"purged\":1", await purge.Content.ReadAsStringAsync(Ct));
    }
}
