using Npgsql;
using static Stellaegis.Documents.Tests.Content;

namespace Stellaegis.Documents.Tests;

/// <summary>
/// The document service on the PostgreSQL store, against a real database: what the schema and the
/// queries guarantee, file name encryption included.
/// </summary>
public sealed class PostgreSqlDocumentStoreTests(PostgreSqlFixture database) : IClassFixture<PostgreSqlFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DocumentsHarness Harness(int activeSlot = 1, int listPageSize = 100)
    {
        Assert.SkipWhen(database.Unavailable is not null, database.Unavailable ?? string.Empty);
        return DocumentsHarness.PostgreSql(database.ConnectionString, activeSlot, listPageSize);
    }

    [Fact]
    public async Task A_document_reads_back_with_its_file_name_decrypted()
    {
        await using var harness = Harness();
        var holder = NewHolder();

        var document = await harness.Documents.CreateAsync(holder, "invoice", Upload("hello", "Facture été n°1.txt"), "alice", cancellationToken: Ct);

        var read = await harness.Documents.GetAsync(holder, document.DocumentUniqueId, Ct);
        Assert.NotNull(read);
        Assert.Equal(("Facture été n°1.txt", "text/plain", 5L, Hash("hello"), 1, DocumentStatus.Active),
            (read.FileName, read.ContentType, read.ContentLength, read.ContentHash, read.VersionNumber, read.Status));
    }

    [Fact]
    public async Task The_file_name_is_stored_encrypted()
    {
        await using var harness = Harness();
        var document = await harness.Documents.CreateAsync(NewHolder(), "invoice", Upload("hello", "secret-name.txt"), "alice", cancellationToken: Ct);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand("select file_name from doc.documents where document_unique_id = @id", connection);
        query.Parameters.AddWithValue("id", document.DocumentUniqueId);
        var stored = (byte[])(await query.ExecuteScalarAsync(Ct))!;

        Assert.DoesNotContain("secret-name", System.Text.Encoding.UTF8.GetString(stored));
    }

    [Fact]
    public async Task Revisions_keep_their_own_file_metadata_and_digest()
    {
        await using var harness = Harness();
        var holder = NewHolder();
        var document = await harness.Documents.CreateAsync(holder, "contract", Upload("c1", "contrat.txt"), "alice", isVersioned: true, cancellationToken: Ct);

        await harness.Documents.UpdateContentAsync(holder, document.DocumentUniqueId, Upload("c2", "contrat-signé.pdf", "application/pdf"), "bob", Ct);

        var versions = await harness.Documents.GetVersionsAsync(holder, document.DocumentUniqueId, Ct);
        Assert.Equal([(1, "contrat.txt", "text/plain", Hash("c1")), (2, "contrat-signé.pdf", "application/pdf", Hash("c2"))],
            versions.Select(v => (v.VersionNumber, v.FileName, v.ContentType, v.ContentHash)));
        Assert.Equal(2, (await harness.Documents.GetAsync(holder, document.DocumentUniqueId, Ct))!.VersionNumber);
    }

    [Fact]
    public async Task Another_holder_reaches_neither_the_document_nor_its_versions()
    {
        await using var harness = Harness();
        var holder = NewHolder();
        var document = await harness.Documents.CreateAsync(holder, "contract", Upload("c1"), "alice", isVersioned: true, cancellationToken: Ct);

        var other = NewHolder();
        Assert.Null(await harness.Documents.GetAsync(other, document.DocumentUniqueId, Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.GetVersionsAsync(other, document.DocumentUniqueId, Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.OpenVersionContentAsync(other, document.DocumentUniqueId, 1, Ct));
    }

    [Fact]
    public async Task The_list_pages_through_active_documents_most_recent_first()
    {
        await using var harness = Harness(listPageSize: 2);
        var holder = NewHolder();
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
            created.Add((await harness.Documents.CreateAsync(holder, "note", Upload($"n{i}"), "alice", cancellationToken: Ct)).DocumentUniqueId);
        await harness.Documents.TrashAsync(holder, created[2], "alice", Ct);

        var seen = new List<Guid>();
        string? token = null;
        do
        {
            var page = await harness.Documents.ListAsync(holder, token, Ct);
            seen.AddRange(page.Documents.Select(d => d.DocumentUniqueId));
            token = page.ContinuationToken;
        }
        while (token is not null);

        Assert.Equal(created.Where((_, i) => i != 2).Reverse(), seen);
    }

    [Fact]
    public async Task Rows_of_the_former_key_slot_stay_readable_after_a_rotation()
    {
        var holder = NewHolder();
        Guid before;
        await using (var first = Harness(activeSlot: 1))
            before = (await first.Documents.CreateAsync(holder, "invoice", Upload("a", "avant.txt"), "alice", cancellationToken: Ct)).DocumentUniqueId;

        await using var rotated = Harness(activeSlot: 2);
        var after = await rotated.Documents.CreateAsync(holder, "invoice", Upload("b", "après.txt"), "alice", cancellationToken: Ct);

        Assert.Equal("avant.txt", (await rotated.Documents.GetAsync(holder, before, Ct))!.FileName);
        Assert.Equal("après.txt", (await rotated.Documents.GetAsync(holder, after.DocumentUniqueId, Ct))!.FileName);
    }

    [Fact]
    public async Task The_purge_deletes_the_holder_rows_and_their_versions()
    {
        await using var harness = Harness();
        var holder = NewHolder();
        var versioned = await harness.Documents.CreateAsync(holder, "contract", Upload("c1"), "alice", isVersioned: true, cancellationToken: Ct);
        await harness.Documents.UpdateContentAsync(holder, versioned.DocumentUniqueId, Upload("c2"), "alice", Ct);
        var trashed = await harness.Documents.CreateAsync(holder, "memo", Upload("m"), "alice", cancellationToken: Ct);
        await harness.Documents.TrashAsync(holder, trashed.DocumentUniqueId, "alice", Ct);

        Assert.Equal(2, await harness.Documents.PurgeHolderAsync(holder, Ct));

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand(
            "select (select count(*) from doc.documents where holder = @holder) + (select count(*) from doc.document_versions where document_unique_id = @id)",
            connection);
        count.Parameters.AddWithValue("holder", holder);
        count.Parameters.AddWithValue("id", versioned.DocumentUniqueId);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(Ct))!);
        Assert.Empty(harness.BlobNames());
    }

    [Fact]
    public async Task A_holder_outside_the_grammar_is_refused()
    {
        await using var harness = Harness();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            harness.Documents.CreateAsync("Not-A-Holder", "invoice", Upload("x"), "alice", cancellationToken: Ct));
    }
}
