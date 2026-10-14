using Stellaegis.Primitives;
using static Stellaegis.Documents.Tests.Content;

namespace Stellaegis.Documents.Tests;

/// <summary>
/// <see cref="IDocumentService"/> through the HTTP client, against the mapped endpoints: a consumer works the
/// same in process or remotely.
/// </summary>
public sealed class DocumentsClientTests(DocumentsHost host) : IClassFixture<DocumentsHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_document_created_through_the_client_reads_back()
    {
        var documents = host.Client("alice");
        var holder = NewHolder();

        var document = await documents.CreateAsync(holder, "invoice", Upload("hello", "Facture été.txt"), "ignored", cancellationToken: Ct);

        Assert.Equal((holder, "Facture été.txt", 1, "alice"), (document.Holder, document.FileName, document.VersionNumber, document.CreatedBy));
        Assert.Equal("hello", await ReadAsync(await documents.OpenContentAsync(holder, document.DocumentUniqueId, Ct)));
        Assert.Equal(5, (await documents.GetAsync(holder, document.DocumentUniqueId, Ct))!.ContentLength);
    }

    [Fact]
    public async Task Absent_documents_map_to_the_in_process_answers()
    {
        var documents = host.Client("alice");
        var holder = NewHolder();
        var document = await documents.CreateAsync(holder, "invoice", Upload("hello"), "ignored", cancellationToken: Ct);

        Assert.Null(await documents.GetAsync(NewHolder(), document.DocumentUniqueId, Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => documents.OpenContentAsync(holder, Guid.NewGuid(), Ct));
        var refused = await Assert.ThrowsAsync<UserFacingException>(() => documents.ListAsync(holder, "not*a*token", Ct));
        Assert.Equal(ErrorType.Validation, refused.Type);
    }

    [Fact]
    public async Task Versions_go_through_the_client()
    {
        var documents = host.Client("alice");
        var holder = NewHolder();
        var document = await documents.CreateAsync(holder, "contract", Upload("c1", "contrat.txt"), "ignored", isVersioned: true, cancellationToken: Ct);

        var updated = await documents.UpdateContentAsync(holder, document.DocumentUniqueId, Upload("c2", "contrat-signé.pdf", "application/pdf"), "ignored", Ct);

        Assert.Equal(2, updated.VersionNumber);
        var versions = await documents.GetVersionsAsync(holder, document.DocumentUniqueId, Ct);
        Assert.Equal([(1, Hash("c1")), (2, Hash("c2"))], versions.Select(v => (v.VersionNumber, v.ContentHash)));
        Assert.Equal("c1", await ReadAsync(await documents.OpenVersionContentAsync(holder, document.DocumentUniqueId, 1, Ct)));
    }

    [Fact]
    public async Task The_list_pages_through_the_client()
    {
        var documents = host.Client("alice");
        var holder = NewHolder();
        for (var i = 0; i < 3; i++)
            await documents.CreateAsync(holder, "note", Upload($"n{i}"), "ignored", cancellationToken: Ct);

        var seen = 0;
        string? token = null;
        do
        {
            var page = await documents.ListAsync(holder, token, Ct);
            seen += page.Documents.Count;
            token = page.ContinuationToken;
        }
        while (token is not null);

        Assert.Equal(3, seen);
    }

    [Fact]
    public async Task A_download_damaged_on_its_way_is_detected()
    {
        var holder = NewHolder();
        var document = await host.Client("alice").CreateAsync(holder, "note", Upload("intact"), "ignored", cancellationToken: Ct);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ReadAsync(await host.Client("alice", damage: true).OpenContentAsync(holder, document.DocumentUniqueId, Ct)));
        Assert.Equal("intact", await ReadAsync(await host.Client("alice").OpenContentAsync(holder, document.DocumentUniqueId, Ct)));
    }

    [Fact]
    public async Task The_trash_and_the_purge_go_through_the_client()
    {
        var documents = host.Client("alice");
        var holder = NewHolder();
        var trashed = await documents.CreateAsync(holder, "note", Upload("t"), "ignored", cancellationToken: Ct);
        await documents.CreateAsync(holder, "note", Upload("k"), "ignored", cancellationToken: Ct);

        await documents.TrashAsync(holder, trashed.DocumentUniqueId, "ignored", Ct);
        Assert.Single((await documents.ListAsync(holder, cancellationToken: Ct)).Documents);

        var refused = await Assert.ThrowsAsync<UserFacingException>(() => documents.PurgeHolderAsync(holder, Ct));
        Assert.Equal(ErrorType.Forbidden, refused.Type);
        Assert.Equal(2, await host.Client("ops", "admin").PurgeHolderAsync(holder, Ct));
    }
}
