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
        var holder = NewHolder();

        var document = await harness.Documents.CreateAsync(holder, "invoice", Upload("hello", "Facture été.txt"), "alice", cancellationToken: Ct);

        var read = await harness.Documents.GetAsync(holder, document.DocumentUniqueId, Ct);
        Assert.NotNull(read);
        Assert.Equal(("Facture été.txt", "text/plain", 5L, Hash("hello"), 1), (read.FileName, read.ContentType, read.ContentLength, read.ContentHash, read.VersionNumber));
        Assert.Equal("alice", read.CreatedBy);
        Assert.Equal("hello", await ReadAsync(await harness.Documents.OpenContentAsync(holder, document.DocumentUniqueId, Ct)));
    }

    [Fact]
    public async Task Content_is_stored_flat_under_a_key_derived_from_the_identity()
    {
        await using var harness = DocumentsHarness.InMemory();

        var document = await harness.Documents.CreateAsync(NewHolder(), "invoice", Upload("hello"), "alice", cancellationToken: Ct);

        Assert.Equal([$"{document.DocumentUniqueId:N}-1"], harness.BlobNames());
    }

    [Fact]
    public async Task An_empty_file_is_a_valid_content()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();

        var document = await harness.Documents.CreateAsync(holder, "note", Upload(string.Empty), "alice", cancellationToken: Ct);

        Assert.Equal(0, document.ContentLength);
        Assert.Equal(string.Empty, await ReadAsync(await harness.Documents.OpenContentAsync(holder, document.DocumentUniqueId, Ct)));
    }

    [Fact]
    public async Task Documents_are_isolated_per_holder()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();
        var document = await harness.Documents.CreateAsync(holder, "invoice", Upload("hello"), "alice", cancellationToken: Ct);

        var other = NewHolder();
        Assert.Null(await harness.Documents.GetAsync(other, document.DocumentUniqueId, Ct));
        Assert.Empty((await harness.Documents.ListAsync(other, cancellationToken: Ct)).Documents);
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.OpenContentAsync(other, document.DocumentUniqueId, Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.UpdateContentAsync(other, document.DocumentUniqueId, Upload("x"), "mallory", Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.TrashAsync(other, document.DocumentUniqueId, "mallory", Ct));
    }

    [Fact]
    public async Task The_platform_holds_documents_under_the_empty_holder()
    {
        await using var harness = DocumentsHarness.InMemory();

        var document = await harness.Documents.CreateAsync(string.Empty, "logo", Upload("svg"), "system", cancellationToken: Ct);

        Assert.NotNull(await harness.Documents.GetAsync(string.Empty, document.DocumentUniqueId, Ct));
    }

    [Fact]
    public async Task A_versioned_document_keeps_each_former_content_as_a_revision()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();
        var document = await harness.Documents.CreateAsync(holder, "contract", Upload("c1", "contrat.txt"), "alice", isVersioned: true, cancellationToken: Ct);

        var updated = await harness.Documents.UpdateContentAsync(holder, document.DocumentUniqueId, Upload("c2", "contrat-signé.pdf", "application/pdf"), "bob", Ct);

        Assert.Equal(2, updated.VersionNumber);
        Assert.Equal("c2", await ReadAsync(await harness.Documents.OpenContentAsync(holder, document.DocumentUniqueId, Ct)));
        var versions = await harness.Documents.GetVersionsAsync(holder, document.DocumentUniqueId, Ct);
        Assert.Equal([(1, "contrat.txt", Hash("c1")), (2, "contrat-signé.pdf", Hash("c2"))], versions.Select(v => (v.VersionNumber, v.FileName, v.ContentHash)));
        Assert.Equal("c1", await ReadAsync(await harness.Documents.OpenVersionContentAsync(holder, document.DocumentUniqueId, 1, Ct)));
        Assert.Equal([$"{document.DocumentUniqueId:N}-1", $"{document.DocumentUniqueId:N}-2"], harness.BlobNames().Order());
    }

    [Fact]
    public async Task A_document_that_is_not_versioned_moves_to_its_next_content_and_drops_the_former()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();
        var document = await harness.Documents.CreateAsync(holder, "memo", Upload("m1"), "alice", cancellationToken: Ct);

        var updated = await harness.Documents.UpdateContentAsync(holder, document.DocumentUniqueId, Upload("m2"), "bob", Ct);

        Assert.Equal(2, updated.VersionNumber);
        Assert.Equal("m2", await ReadAsync(await harness.Documents.OpenContentAsync(holder, document.DocumentUniqueId, Ct)));
        Assert.Empty(await harness.Documents.GetVersionsAsync(holder, document.DocumentUniqueId, Ct));
        Assert.Equal([$"{document.DocumentUniqueId:N}-2"], harness.BlobNames());
    }

    [Fact]
    public async Task Identical_content_changes_nothing_even_under_another_name()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();
        var document = await harness.Documents.CreateAsync(holder, "contract", Upload("same", "a.txt"), "alice", isVersioned: true, cancellationToken: Ct);

        var again = await harness.Documents.UpdateContentAsync(holder, document.DocumentUniqueId, Upload("same", "b.txt"), "bob", Ct);

        Assert.Equal((1, "a.txt"), (again.VersionNumber, again.FileName));
        Assert.Single(await harness.Documents.GetVersionsAsync(holder, document.DocumentUniqueId, Ct));
        Assert.Equal([$"{document.DocumentUniqueId:N}-1"], harness.BlobNames());
    }

    [Fact]
    public async Task The_current_digest_announced_skips_reading_the_content()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();
        var document = await harness.Documents.CreateAsync(holder, "archive", Upload("same"), "alice", cancellationToken: Ct);
        var content = new GeneratedContent(1024);

        var again = await harness.Documents.UpdateContentAsync(holder, document.DocumentUniqueId,
            new DocumentUpload { Content = content, FileName = "x.bin", ContentType = "application/octet-stream", ExpectedContentHash = Hash("same") }, "bob", Ct);

        Assert.Equal(1, again.VersionNumber);
        Assert.Equal(0, content.Produced);
    }

    [Fact]
    public async Task Content_that_does_not_match_its_digest_is_refused_and_leaves_nothing()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();

        var refused = await Assert.ThrowsAsync<UserFacingException>(() =>
            harness.Documents.CreateAsync(holder, "invoice", Upload("hello", expectedHash: Hash("something else")), "alice", cancellationToken: Ct));

        Assert.Equal(ErrorType.Validation, refused.Type);
        Assert.Empty((await harness.Documents.ListAsync(holder, cancellationToken: Ct)).Documents);
        Assert.Empty(harness.BlobNames());
    }

    [Fact]
    public async Task A_wrong_digest_on_a_change_keeps_the_current_content()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();
        var document = await harness.Documents.CreateAsync(holder, "memo", Upload("m1"), "alice", cancellationToken: Ct);

        await Assert.ThrowsAsync<UserFacingException>(() =>
            harness.Documents.UpdateContentAsync(holder, document.DocumentUniqueId, Upload("m2", expectedHash: Hash("other")), "bob", Ct));

        Assert.Equal("m1", await ReadAsync(await harness.Documents.OpenContentAsync(holder, document.DocumentUniqueId, Ct)));
        Assert.Equal([$"{document.DocumentUniqueId:N}-1"], harness.BlobNames());
    }

    [Theory]
    [InlineData(256, 10)]
    [InlineData(10, 256)]
    public async Task File_name_and_media_type_are_bounded(int fileNameLength, int contentTypeLength)
    {
        await using var harness = DocumentsHarness.InMemory();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => harness.Documents.CreateAsync(NewHolder(), "invoice",
            Upload("x", new string('f', fileNameLength), new string('t', contentTypeLength)), "alice", cancellationToken: Ct));
    }

    [Fact]
    public async Task A_document_indexed_without_content_answers_as_absent()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();
        var document = await harness.Documents.CreateAsync(holder, "invoice", Upload("hello"), "alice", cancellationToken: Ct);
        foreach (var file in Directory.GetFiles(harness.BlobRoot, "*", SearchOption.AllDirectories))
            File.Delete(file);

        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.OpenContentAsync(holder, document.DocumentUniqueId, Ct));
    }

    [Fact]
    public async Task The_list_pages_through_active_documents_most_recent_first()
    {
        await using var harness = DocumentsHarness.InMemory(listPageSize: 2);
        var holder = NewHolder();
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
            created.Add((await harness.Documents.CreateAsync(holder, "note", Upload($"n{i}"), "alice", cancellationToken: Ct)).DocumentUniqueId);

        var seen = new List<Guid>();
        var pages = 0;
        string? token = null;
        do
        {
            var page = await harness.Documents.ListAsync(holder, token, Ct);
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

        var refused = await Assert.ThrowsAsync<UserFacingException>(() => harness.Documents.ListAsync(NewHolder(), token, Ct));

        Assert.Equal(ErrorType.Validation, refused.Type);
    }

    [Fact]
    public async Task A_trashed_document_leaves_the_list_and_keeps_its_content()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();
        var document = await harness.Documents.CreateAsync(holder, "invoice", Upload("hello"), "alice", cancellationToken: Ct);

        await harness.Documents.TrashAsync(holder, document.DocumentUniqueId, "bob", Ct);

        Assert.Empty((await harness.Documents.ListAsync(holder, cancellationToken: Ct)).Documents);
        Assert.Equal(DocumentStatus.Trashed, (await harness.Documents.GetAsync(holder, document.DocumentUniqueId, Ct))!.Status);
        Assert.Single(harness.BlobNames());
        await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            harness.Documents.UpdateContentAsync(holder, document.DocumentUniqueId, Upload("again"), "bob", Ct));
    }

    [Fact]
    public async Task The_purge_deletes_every_document_of_the_holder_and_nothing_else()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();
        var versioned = await harness.Documents.CreateAsync(holder, "contract", Upload("c1"), "alice", isVersioned: true, cancellationToken: Ct);
        await harness.Documents.UpdateContentAsync(holder, versioned.DocumentUniqueId, Upload("c2"), "alice", Ct);
        var trashed = await harness.Documents.CreateAsync(holder, "memo", Upload("m"), "alice", cancellationToken: Ct);
        await harness.Documents.TrashAsync(holder, trashed.DocumentUniqueId, "alice", Ct);
        var kept = await harness.Documents.CreateAsync(NewHolder(), "invoice", Upload("other"), "alice", cancellationToken: Ct);

        Assert.Equal(2, await harness.Documents.PurgeHolderAsync(holder, Ct));

        Assert.Null(await harness.Documents.GetAsync(holder, versioned.DocumentUniqueId, Ct));
        Assert.Equal([$"{kept.DocumentUniqueId:N}-1"], harness.BlobNames());
        Assert.Equal(0, await harness.Documents.PurgeHolderAsync(holder, Ct));
    }

    [Fact]
    public async Task The_purge_goes_through_more_documents_than_one_batch()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();
        for (var i = 0; i < 230; i++)
            await harness.Documents.CreateAsync(holder, "note", Upload($"n{i}"), "alice", cancellationToken: Ct);

        Assert.Equal(230, await harness.Documents.PurgeHolderAsync(holder, Ct));
        Assert.Empty(harness.BlobNames());
    }

    [Fact]
    public async Task The_platform_is_never_purged()
    {
        await using var harness = DocumentsHarness.InMemory();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => harness.Documents.PurgeHolderAsync(string.Empty, Ct));
    }

    [Fact]
    public async Task Creation_and_changes_publish_an_indexed_event_with_the_holder()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();

        var document = await harness.Documents.CreateAsync(holder, "invoice", Upload("v1"), "alice", isVersioned: true, cancellationToken: Ct);
        await harness.Documents.UpdateContentAsync(holder, document.DocumentUniqueId, Upload("v2"), "alice", Ct);

        var events = harness.Events.Published.OfType<DocumentIndexedEvent>().ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal((holder, document.DocumentUniqueId, "invoice", true), (e.Holder, e.DocumentUniqueId, e.DocumentType, e.IsVersioned)));
        Assert.Equal([Hash("v1"), Hash("v2")], events.Select(e => e.ContentHash));
    }

    [Fact]
    public async Task Identical_content_publishes_no_event()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();
        var document = await harness.Documents.CreateAsync(holder, "invoice", Upload("same"), "alice", cancellationToken: Ct);

        await harness.Documents.UpdateContentAsync(holder, document.DocumentUniqueId, Upload("same"), "alice", Ct);

        Assert.Single(harness.Events.Published);
    }

    [Fact]
    public async Task Trashing_publishes_a_trashed_event()
    {
        await using var harness = DocumentsHarness.InMemory();
        var holder = NewHolder();
        var document = await harness.Documents.CreateAsync(holder, "invoice", Upload("x"), "alice", cancellationToken: Ct);

        await harness.Documents.TrashAsync(holder, document.DocumentUniqueId, "alice", Ct);

        var trashed = Assert.Single(harness.Events.Published.OfType<DocumentTrashedEvent>());
        Assert.Equal((holder, document.DocumentUniqueId), (trashed.Holder, trashed.DocumentUniqueId));
    }
}
