using Stellaegis.Primitives;
using static Stellaegis.Documents.Tests.Content;

namespace Stellaegis.Documents.Tests;

/// <summary>
/// The document service on the in-memory store and the filesystem blob store: what a consumer sees, and
/// what lands on disk.
/// </summary>
public sealed class DocumentServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_created_document_reads_back_with_its_file_metadata()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();

        var document = await harness.Documents.CreateAsync(compartment, "invoice", Upload("hello", "Facture été.txt"), Alice, cancellationToken: Ct);

        var read = await harness.Documents.GetAsync(compartment, document.DocumentUniqueId, Ct);
        Assert.NotNull(read);
        Assert.Equal(("Facture été.txt", "text/plain", 5L, Hash("hello"), 1), (read.FileName, read.ContentType, read.ContentLength, read.ContentHash, read.VersionNumber));
        Assert.Equal(Alice, read.CreatedBy);
        Assert.Equal("hello", await ReadAsync(await harness.Documents.OpenContentAsync(compartment, document.DocumentUniqueId, Ct)));
    }

    [Fact]
    public async Task Content_is_stored_flat_under_a_key_derived_from_the_identity()
    {
        await using var harness = DocumentsHarness.InMemory();

        var document = await harness.Documents.CreateAsync(NewCompartment(), "invoice", Upload("hello"), Alice, cancellationToken: Ct);

        Assert.Equal([$"{document.DocumentUniqueId:N}-1"], harness.BlobNames());
    }

    [Fact]
    public async Task Content_is_stored_under_the_key_prefix_in_the_directory_of_its_space()
    {
        await using var harness = DocumentsHarness.InMemory(keyPrefix: "ged");
        var compartment = NewCompartment();

        var inCompartment = await harness.Documents.CreateAsync(compartment, "invoice", Upload("hello"), Alice, cancellationToken: Ct);
        var shared = await harness.Documents.CreateAsync(Space.Shared, "invoice", Upload("hello"), Alice, cancellationToken: Ct);

        Assert.Equal(
            [$"_shared/ged/{shared.DocumentUniqueId:N}-1", $"{compartment}/ged/{inCompartment.DocumentUniqueId:N}-1"],
            harness.BlobPaths().Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Documents")]
    [InlineData("documents/")]
    [InlineData("nul")]
    public void A_key_prefix_the_blob_store_refuses_fails_when_the_service_is_built(string keyPrefix)
        => Assert.ThrowsAny<ArgumentException>(() => DocumentsHarness.InMemory(keyPrefix: keyPrefix));

    [Fact]
    public async Task An_empty_file_is_a_valid_content()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();

        var document = await harness.Documents.CreateAsync(compartment, "note", Upload(string.Empty), Alice, cancellationToken: Ct);

        Assert.Equal(0, document.ContentLength);
        Assert.Equal(string.Empty, await ReadAsync(await harness.Documents.OpenContentAsync(compartment, document.DocumentUniqueId, Ct)));
    }

    [Fact]
    public async Task Documents_are_isolated_per_compartment()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "invoice", Upload("hello"), Alice, cancellationToken: Ct);

        var other = NewCompartment();
        Assert.Null(await harness.Documents.GetAsync(other, document.DocumentUniqueId, Ct));
        Assert.Empty((await harness.Documents.ListAsync(other, cancellationToken: Ct)).Documents);
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.OpenContentAsync(other, document.DocumentUniqueId, Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.UpdateContentAsync(other, document.DocumentUniqueId, Upload("x"), Mallory, Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.TrashAsync(other, document.DocumentUniqueId, Mallory, Ct));
    }

    [Fact]
    public async Task The_shared_space_holds_the_documents_that_belong_to_no_compartment()
    {
        await using var harness = DocumentsHarness.InMemory();

        var document = await harness.Documents.CreateAsync(Space.Shared, "logo", Upload("svg"), Actor.Parse("system"), cancellationToken: Ct);

        Assert.Equal(Space.Shared, (await harness.Documents.GetAsync(Space.Shared, document.DocumentUniqueId, Ct))!.Space);
        Assert.Equal("svg", await ReadAsync(await harness.Documents.OpenContentAsync(Space.Shared, document.DocumentUniqueId, Ct)));
        var stored = Assert.Single(Directory.GetFiles(harness.BlobRoot, "*", SearchOption.AllDirectories));
        Assert.Equal("_shared", Path.GetRelativePath(harness.BlobRoot, stored).Split(Path.DirectorySeparatorChar)[0]);
    }

    [Fact]
    public async Task The_shared_space_and_a_compartment_never_see_each_other()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        var shared = await harness.Documents.CreateAsync(Space.Shared, "logo", Upload("svg"), Alice, cancellationToken: Ct);
        var owned = await harness.Documents.CreateAsync(compartment, "invoice", Upload("hello"), Alice, cancellationToken: Ct);

        Assert.Null(await harness.Documents.GetAsync(compartment, shared.DocumentUniqueId, Ct));
        Assert.Null(await harness.Documents.GetAsync(Space.Shared, owned.DocumentUniqueId, Ct));
        Assert.Equal([owned.DocumentUniqueId], (await harness.Documents.ListAsync(compartment, cancellationToken: Ct)).Documents.Select(d => d.DocumentUniqueId));
        Assert.Contains(shared.DocumentUniqueId, (await harness.Documents.ListAsync(Space.Shared, cancellationToken: Ct)).Documents.Select(d => d.DocumentUniqueId));
        Assert.DoesNotContain(owned.DocumentUniqueId, (await harness.Documents.ListAsync(Space.Shared, cancellationToken: Ct)).Documents.Select(d => d.DocumentUniqueId));
    }

    [Fact]
    public async Task A_versioned_document_keeps_each_former_content_as_a_revision()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "contract", Upload("c1", "contrat.txt"), Alice, isVersioned: true, cancellationToken: Ct);

        var updated = await harness.Documents.UpdateContentAsync(compartment, document.DocumentUniqueId, Upload("c2", "contrat-signé.pdf", "application/pdf"), Bob, Ct);

        Assert.Equal(2, updated.VersionNumber);
        Assert.Equal("c2", await ReadAsync(await harness.Documents.OpenContentAsync(compartment, document.DocumentUniqueId, Ct)));
        var versions = await harness.Documents.GetVersionsAsync(compartment, document.DocumentUniqueId, Ct);
        Assert.Equal([(1, "contrat.txt", Hash("c1")), (2, "contrat-signé.pdf", Hash("c2"))], versions.Select(v => (v.VersionNumber, v.FileName, v.ContentHash)));
        Assert.Equal("c1", await ReadAsync(await harness.Documents.OpenVersionContentAsync(compartment, document.DocumentUniqueId, 1, Ct)));
        Assert.Equal([$"{document.DocumentUniqueId:N}-1", $"{document.DocumentUniqueId:N}-2"], harness.BlobNames().Order());
    }

    [Fact]
    public async Task A_document_that_is_not_versioned_moves_to_its_next_content_and_drops_the_former()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "memo", Upload("m1"), Alice, cancellationToken: Ct);

        var updated = await harness.Documents.UpdateContentAsync(compartment, document.DocumentUniqueId, Upload("m2"), Bob, Ct);

        Assert.Equal(2, updated.VersionNumber);
        Assert.Equal("m2", await ReadAsync(await harness.Documents.OpenContentAsync(compartment, document.DocumentUniqueId, Ct)));
        Assert.Empty(await harness.Documents.GetVersionsAsync(compartment, document.DocumentUniqueId, Ct));
        Assert.Equal([$"{document.DocumentUniqueId:N}-2"], harness.BlobNames());
    }

    [Fact]
    public async Task Identical_content_changes_nothing_even_under_another_name()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "contract", Upload("same", "a.txt"), Alice, isVersioned: true, cancellationToken: Ct);

        var again = await harness.Documents.UpdateContentAsync(compartment, document.DocumentUniqueId, Upload("same", "b.txt"), Bob, Ct);

        Assert.Equal((1, "a.txt"), (again.VersionNumber, again.FileName));
        Assert.Single(await harness.Documents.GetVersionsAsync(compartment, document.DocumentUniqueId, Ct));
        Assert.Equal([$"{document.DocumentUniqueId:N}-1"], harness.BlobNames());
    }

    [Fact]
    public async Task The_current_digest_announced_skips_reading_the_content()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "archive", Upload("same"), Alice, cancellationToken: Ct);
        var content = new GeneratedContent(1024);

        var again = await harness.Documents.UpdateContentAsync(compartment, document.DocumentUniqueId,
            new DocumentUpload { Content = content, FileName = "x.bin", ContentType = "application/octet-stream", ExpectedContentHash = Hash("same") }, Bob, Ct);

        Assert.Equal(1, again.VersionNumber);
        Assert.Equal(0, content.Produced);
    }

    [Fact]
    public async Task Content_that_does_not_match_its_digest_is_refused_and_leaves_nothing()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();

        var refused = await Assert.ThrowsAsync<UserFacingException>(() =>
            harness.Documents.CreateAsync(compartment, "invoice", Upload("hello", expectedHash: Hash("something else")), Alice, cancellationToken: Ct));

        Assert.Equal(ErrorType.Validation, refused.Type);
        Assert.Empty((await harness.Documents.ListAsync(compartment, cancellationToken: Ct)).Documents);
        Assert.Empty(harness.BlobNames());
    }

    [Fact]
    public async Task A_wrong_digest_on_a_change_keeps_the_current_content()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "memo", Upload("m1"), Alice, cancellationToken: Ct);

        await Assert.ThrowsAsync<UserFacingException>(() =>
            harness.Documents.UpdateContentAsync(compartment, document.DocumentUniqueId, Upload("m2", expectedHash: Hash("other")), Bob, Ct));

        Assert.Equal("m1", await ReadAsync(await harness.Documents.OpenContentAsync(compartment, document.DocumentUniqueId, Ct)));
        Assert.Equal([$"{document.DocumentUniqueId:N}-1"], harness.BlobNames());
    }

    [Theory]
    [InlineData(256, 10)]
    [InlineData(10, 256)]
    public async Task File_name_and_media_type_are_bounded(int fileNameLength, int contentTypeLength)
    {
        await using var harness = DocumentsHarness.InMemory();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => harness.Documents.CreateAsync(NewCompartment(), "invoice",
            Upload("x", new string('f', fileNameLength), new string('t', contentTypeLength)), Alice, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_document_indexed_without_content_answers_as_absent()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "invoice", Upload("hello"), Alice, cancellationToken: Ct);
        foreach (var file in Directory.GetFiles(harness.BlobRoot, "*", SearchOption.AllDirectories))
            File.Delete(file);

        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.OpenContentAsync(compartment, document.DocumentUniqueId, Ct));
    }

    [Fact]
    public async Task The_list_pages_through_active_documents_most_recent_first()
    {
        await using var harness = DocumentsHarness.InMemory(listPageSize: 2);
        var compartment = NewCompartment();
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
            created.Add((await harness.Documents.CreateAsync(compartment, "note", Upload($"n{i}"), Alice, cancellationToken: Ct)).DocumentUniqueId);

        var seen = new List<Guid>();
        var pages = 0;
        string? token = null;
        do
        {
            var page = await harness.Documents.ListAsync(compartment, token, Ct);
            Assert.True(page.Documents.Count <= 2);
            seen.AddRange(page.Documents.Select(d => d.DocumentUniqueId));
            token = page.ContinuationToken;
            pages++;
        }
        while (token is not null);

        Assert.Equal(3, pages);
        Assert.Equal(created.AsEnumerable().Reverse(), seen);
    }

    [Theory]
    [InlineData("not*a*token")]
    [InlineData("AAAA")]
    public async Task A_malformed_continuation_token_is_a_validation_error(string token)
    {
        await using var harness = DocumentsHarness.InMemory();

        var refused = await Assert.ThrowsAsync<UserFacingException>(() => harness.Documents.ListAsync(NewCompartment(), token, Ct));

        Assert.Equal(ErrorType.Validation, refused.Type);
    }

    [Fact]
    public async Task A_trashed_document_leaves_the_list_and_keeps_its_content()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "invoice", Upload("hello"), Alice, cancellationToken: Ct);

        await harness.Documents.TrashAsync(compartment, document.DocumentUniqueId, Bob, Ct);

        Assert.Empty((await harness.Documents.ListAsync(compartment, cancellationToken: Ct)).Documents);
        Assert.Equal(DocumentStatus.Trashed, (await harness.Documents.GetAsync(compartment, document.DocumentUniqueId, Ct))!.Status);
        Assert.Single(harness.BlobNames());
        await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            harness.Documents.UpdateContentAsync(compartment, document.DocumentUniqueId, Upload("again"), Bob, Ct));
    }

    [Fact]
    public async Task The_purge_deletes_every_document_of_the_compartment_and_nothing_else()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        var versioned = await harness.Documents.CreateAsync(compartment, "contract", Upload("c1"), Alice, isVersioned: true, cancellationToken: Ct);
        await harness.Documents.UpdateContentAsync(compartment, versioned.DocumentUniqueId, Upload("c2"), Alice, Ct);
        var trashed = await harness.Documents.CreateAsync(compartment, "memo", Upload("m"), Alice, cancellationToken: Ct);
        await harness.Documents.TrashAsync(compartment, trashed.DocumentUniqueId, Alice, Ct);
        var kept = await harness.Documents.CreateAsync(NewCompartment(), "invoice", Upload("other"), Alice, cancellationToken: Ct);

        Assert.Equal(2, await harness.Documents.PurgeCompartmentAsync(compartment, Ct));

        Assert.Null(await harness.Documents.GetAsync(compartment, versioned.DocumentUniqueId, Ct));
        Assert.Equal([$"{kept.DocumentUniqueId:N}-1"], harness.BlobNames());
        Assert.Equal(0, await harness.Documents.PurgeCompartmentAsync(compartment, Ct));
    }

    [Fact]
    public async Task The_purge_goes_through_more_documents_than_one_batch()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        for (var i = 0; i < 230; i++)
            await harness.Documents.CreateAsync(compartment, "note", Upload($"n{i}"), Alice, cancellationToken: Ct);

        Assert.Equal(230, await harness.Documents.PurgeCompartmentAsync(compartment, Ct));
        Assert.Empty(harness.BlobNames());
    }

    [Fact]
    public async Task A_purge_leaves_the_shared_space_untouched()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        await harness.Documents.CreateAsync(compartment, "invoice", Upload("hello"), Alice, cancellationToken: Ct);
        var shared = await harness.Documents.CreateAsync(Space.Shared, "logo", Upload("svg"), Alice, cancellationToken: Ct);

        Assert.Equal(1, await harness.Documents.PurgeCompartmentAsync(compartment, Ct));

        Assert.NotNull(await harness.Documents.GetAsync(Space.Shared, shared.DocumentUniqueId, Ct));
        Assert.Equal([$"{shared.DocumentUniqueId:N}-1"], harness.BlobNames());
    }

    [Fact]
    public async Task Creation_and_changes_publish_an_indexed_event_with_the_compartment()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();

        var document = await harness.Documents.CreateAsync(compartment, "invoice", Upload("v1"), Alice, isVersioned: true, cancellationToken: Ct);
        await harness.Documents.UpdateContentAsync(compartment, document.DocumentUniqueId, Upload("v2"), Alice, Ct);

        var events = harness.Events.Published.OfType<DocumentIndexedEvent>().ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal((compartment, false, document.DocumentUniqueId, "invoice", true), (e.Compartment, e.IsShared, e.DocumentUniqueId, e.DocumentType, e.IsVersioned)));
        Assert.Equal([Hash("v1"), Hash("v2")], events.Select(e => e.ContentHash));
    }

    [Fact]
    public async Task Identical_content_publishes_no_event()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "invoice", Upload("same"), Alice, cancellationToken: Ct);

        await harness.Documents.UpdateContentAsync(compartment, document.DocumentUniqueId, Upload("same"), Alice, Ct);

        Assert.Single(harness.Events.Published);
    }

    [Fact]
    public async Task Trashing_publishes_a_trashed_event()
    {
        await using var harness = DocumentsHarness.InMemory();
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "invoice", Upload("x"), Alice, cancellationToken: Ct);

        await harness.Documents.TrashAsync(compartment, document.DocumentUniqueId, Alice, Ct);

        var trashed = Assert.Single(harness.Events.Published.OfType<DocumentTrashedEvent>());
        Assert.Equal((compartment, false, document.DocumentUniqueId), (trashed.Compartment, trashed.IsShared, trashed.DocumentUniqueId));
    }

    [Fact]
    public async Task The_events_of_a_shared_document_say_so_without_a_compartment()
    {
        await using var harness = DocumentsHarness.InMemory();
        var document = await harness.Documents.CreateAsync(Space.Shared, "logo", Upload("svg"), Alice, cancellationToken: Ct);

        await harness.Documents.TrashAsync(Space.Shared, document.DocumentUniqueId, Alice, Ct);

        var indexed = Assert.Single(harness.Events.Published.OfType<DocumentIndexedEvent>());
        var trashed = Assert.Single(harness.Events.Published.OfType<DocumentTrashedEvent>());
        Assert.Equal(((Compartment?)null, true), (indexed.Compartment, indexed.IsShared));
        Assert.Equal(((Compartment?)null, true), (trashed.Compartment, trashed.IsShared));
    }
}
