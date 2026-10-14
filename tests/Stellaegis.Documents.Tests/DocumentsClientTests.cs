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

    // The server records the authenticated caller: the client does not send the actor it is given.
    private static readonly Actor Ignored = Actor.Parse("ignored");

    [Fact]
    public async Task A_document_created_through_the_client_reads_back()
    {
        var documents = host.Client("alice");
        var compartment = NewCompartment();

        var document = await documents.CreateAsync(compartment, "invoice", Upload("hello", "Facture été.txt"), Ignored, cancellationToken: Ct);

        Assert.Equal((Space.Of(compartment), "Facture été.txt", 1, Actor.Parse("alice")), (document.Space, document.FileName, document.VersionNumber, document.CreatedBy));
        Assert.Equal("hello", await ReadAsync(await documents.OpenContentAsync(compartment, document.DocumentUniqueId, Ct)));
        Assert.Equal(5, (await documents.GetAsync(compartment, document.DocumentUniqueId, Ct))!.ContentLength);
    }

    [Fact]
    public async Task Absent_documents_map_to_the_in_process_answers()
    {
        var documents = host.Client("alice");
        var compartment = NewCompartment();
        var document = await documents.CreateAsync(compartment, "invoice", Upload("hello"), Ignored, cancellationToken: Ct);

        Assert.Null(await documents.GetAsync(NewCompartment(), document.DocumentUniqueId, Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => documents.OpenContentAsync(compartment, Guid.NewGuid(), Ct));
        var refused = await Assert.ThrowsAsync<UserFacingException>(() => documents.ListAsync(compartment, "not*a*token", Ct));
        Assert.Equal(ErrorType.Validation, refused.Type);
    }

    [Fact]
    public async Task Versions_go_through_the_client()
    {
        var documents = host.Client("alice");
        var compartment = NewCompartment();
        var document = await documents.CreateAsync(compartment, "contract", Upload("c1", "contrat.txt"), Ignored, isVersioned: true, cancellationToken: Ct);

        var updated = await documents.UpdateContentAsync(compartment, document.DocumentUniqueId, Upload("c2", "contrat-signé.pdf", "application/pdf"), Ignored, Ct);

        Assert.Equal(2, updated.VersionNumber);
        var versions = await documents.GetVersionsAsync(compartment, document.DocumentUniqueId, Ct);
        Assert.Equal([(1, Hash("c1")), (2, Hash("c2"))], versions.Select(v => (v.VersionNumber, v.ContentHash)));
        Assert.Equal("c1", await ReadAsync(await documents.OpenVersionContentAsync(compartment, document.DocumentUniqueId, 1, Ct)));
    }

    [Fact]
    public async Task The_list_pages_through_the_client()
    {
        var documents = host.Client("alice");
        var compartment = NewCompartment();
        for (var i = 0; i < 3; i++)
            await documents.CreateAsync(compartment, "note", Upload($"n{i}"), Ignored, cancellationToken: Ct);

        var seen = 0;
        string? token = null;
        do
        {
            var page = await documents.ListAsync(compartment, token, Ct);
            seen += page.Documents.Count;
            token = page.ContinuationToken;
        }
        while (token is not null);

        Assert.Equal(3, seen);
    }

    [Fact]
    public async Task A_download_damaged_on_its_way_is_detected()
    {
        var compartment = NewCompartment();
        var document = await host.Client("alice").CreateAsync(compartment, "note", Upload("intact"), Ignored, cancellationToken: Ct);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ReadAsync(await host.Client("alice", damage: true).OpenContentAsync(compartment, document.DocumentUniqueId, Ct)));
        Assert.Equal("intact", await ReadAsync(await host.Client("alice").OpenContentAsync(compartment, document.DocumentUniqueId, Ct)));
    }

    [Fact]
    public async Task The_trash_and_the_purge_go_through_the_client()
    {
        var documents = host.Client("alice");
        var compartment = NewCompartment();
        var trashed = await documents.CreateAsync(compartment, "note", Upload("t"), Ignored, cancellationToken: Ct);
        await documents.CreateAsync(compartment, "note", Upload("k"), Ignored, cancellationToken: Ct);

        await documents.TrashAsync(compartment, trashed.DocumentUniqueId, Ignored, Ct);
        Assert.Single((await documents.ListAsync(compartment, cancellationToken: Ct)).Documents);

        var refused = await Assert.ThrowsAsync<UserFacingException>(() => documents.PurgeCompartmentAsync(compartment, Ct));
        Assert.Equal(ErrorType.Forbidden, refused.Type);
        Assert.Equal(2, await host.Client("ops", "admin").PurgeCompartmentAsync(compartment, Ct));
    }
}
