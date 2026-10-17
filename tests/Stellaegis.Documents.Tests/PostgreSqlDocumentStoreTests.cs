using Npgsql;
using Stellaegis.Primitives;
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
        var compartment = NewCompartment();

        var document = await harness.Documents.CreateAsync(compartment, "invoice", Upload("hello", "Facture été n°1.txt"), Alice, cancellationToken: Ct);

        var read = await harness.Documents.GetAsync(compartment, document.DocumentUniqueId, Alice, Ct);
        Assert.NotNull(read);
        Assert.Equal(("Facture été n°1.txt", "text/plain", 5L, Hash("hello"), 1, DocumentStatus.Active),
            (read.FileName, read.ContentType, read.ContentLength, read.ContentHash, read.VersionNumber, read.Status));
    }

    [Fact]
    public async Task The_file_name_is_stored_encrypted()
    {
        await using var harness = Harness();
        var document = await harness.Documents.CreateAsync(NewCompartment(), "invoice", Upload("hello", "secret-name.txt"), Alice, cancellationToken: Ct);

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
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "contract", Upload("c1", "contrat.txt"), Alice, isVersioned: true, cancellationToken: Ct);

        await harness.Documents.UpdateContentAsync(compartment, document.DocumentUniqueId, Upload("c2", "contrat-signé.pdf", "application/pdf"), Bob, Ct);

        var versions = await harness.Documents.GetVersionsAsync(compartment, document.DocumentUniqueId, Alice, Ct);
        Assert.Equal([(1, "contrat.txt", "text/plain", Hash("c1")), (2, "contrat-signé.pdf", "application/pdf", Hash("c2"))],
            versions.Select(v => (v.VersionNumber, v.FileName, v.ContentType, v.ContentHash)));
        Assert.Equal(2, (await harness.Documents.GetAsync(compartment, document.DocumentUniqueId, Alice, Ct))!.VersionNumber);
    }

    [Fact]
    public async Task Another_compartment_reaches_neither_the_document_nor_its_versions()
    {
        await using var harness = Harness();
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "contract", Upload("c1"), Alice, isVersioned: true, cancellationToken: Ct);

        var other = NewCompartment();
        Assert.Null(await harness.Documents.GetAsync(other, document.DocumentUniqueId, Alice, Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.GetVersionsAsync(other, document.DocumentUniqueId, Alice, Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.OpenVersionContentAsync(other, document.DocumentUniqueId, 1, Alice, Ct));
    }

    [Fact]
    public async Task The_list_pages_through_active_documents_most_recent_first()
    {
        await using var harness = Harness(listPageSize: 2);
        var compartment = NewCompartment();
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
            created.Add((await harness.Documents.CreateAsync(compartment, "note", Upload($"n{i}"), Alice, cancellationToken: Ct)).DocumentUniqueId);
        await harness.Documents.TrashAsync(compartment, created[2], Alice, Ct);

        var seen = new List<Guid>();
        string? token = null;
        do
        {
            var page = await harness.Documents.ListAsync(compartment, Alice, token, Ct);
            seen.AddRange(page.Documents.Select(d => d.DocumentUniqueId));
            token = page.ContinuationToken;
        }
        while (token is not null);

        Assert.Equal(created.Where((_, i) => i != 2).Reverse(), seen);
    }

    [Fact]
    public async Task Rows_of_the_former_key_slot_stay_readable_after_a_rotation()
    {
        var compartment = NewCompartment();
        Guid before;
        await using (var first = Harness(activeSlot: 1))
            before = (await first.Documents.CreateAsync(compartment, "invoice", Upload("a", "avant.txt"), Alice, cancellationToken: Ct)).DocumentUniqueId;

        await using var rotated = Harness(activeSlot: 2);
        var after = await rotated.Documents.CreateAsync(compartment, "invoice", Upload("b", "après.txt"), Alice, cancellationToken: Ct);

        Assert.Equal("avant.txt", (await rotated.Documents.GetAsync(compartment, before, Alice, Ct))!.FileName);
        Assert.Equal("après.txt", (await rotated.Documents.GetAsync(compartment, after.DocumentUniqueId, Alice, Ct))!.FileName);
    }

    [Fact]
    public async Task The_purge_deletes_the_compartment_rows_and_their_versions()
    {
        await using var harness = Harness();
        var compartment = NewCompartment();
        var versioned = await harness.Documents.CreateAsync(compartment, "contract", Upload("c1"), Alice, isVersioned: true, cancellationToken: Ct);
        await harness.Documents.UpdateContentAsync(compartment, versioned.DocumentUniqueId, Upload("c2"), Alice, Ct);
        var trashed = await harness.Documents.CreateAsync(compartment, "memo", Upload("m"), Alice, cancellationToken: Ct);
        await harness.Documents.TrashAsync(compartment, trashed.DocumentUniqueId, Alice, Ct);

        Assert.Equal(2, await harness.Documents.PurgeCompartmentAsync(compartment, Purger, Ct));

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand(
            "select (select count(*) from doc.documents where compartment = @compartment) + (select count(*) from doc.document_versions where document_unique_id = @id)",
            connection);
        count.Parameters.AddWithValue("compartment", compartment.Value);
        count.Parameters.AddWithValue("id", versioned.DocumentUniqueId);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(Ct))!);
        Assert.Empty(harness.BlobNames());
    }

    [Fact]
    public async Task The_shared_space_is_stored_apart_from_every_compartment()
    {
        await using var harness = Harness();
        var compartment = NewCompartment();
        var shared = await harness.Documents.CreateAsync(Space.Shared, "contract", Upload("c1"), Alice, isVersioned: true, cancellationToken: Ct);
        await harness.Documents.UpdateContentAsync(Space.Shared, shared.DocumentUniqueId, Upload("c2"), Bob, Ct);
        var owned = await harness.Documents.CreateAsync(compartment, "invoice", Upload("hello"), Alice, cancellationToken: Ct);

        var read = await harness.Documents.GetAsync(Space.Shared, shared.DocumentUniqueId, Alice, Ct);
        Assert.Equal((Space.Shared, 2, Bob), (read!.Space, read.VersionNumber, read.ModifiedBy));
        Assert.Equal(2, (await harness.Documents.GetVersionsAsync(Space.Shared, shared.DocumentUniqueId, Alice, Ct)).Count);
        Assert.Null(await harness.Documents.GetAsync(compartment, shared.DocumentUniqueId, Alice, Ct));
        Assert.Null(await harness.Documents.GetAsync(Space.Shared, owned.DocumentUniqueId, Alice, Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.GetVersionsAsync(compartment, shared.DocumentUniqueId, Alice, Ct));
        Assert.DoesNotContain(owned.DocumentUniqueId, (await harness.Documents.ListAsync(Space.Shared, Alice, cancellationToken: Ct)).Documents.Select(d => d.DocumentUniqueId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Not-A-Compartment")]
    [InlineData("1abc")]
    [InlineData("abc-")]
    public async Task The_schema_refuses_a_compartment_outside_the_grammar(string compartment)
    {
        await using var harness = Harness();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand(
            """
            insert into doc.documents (compartment, document_type, content_hash, file_name, content_type, content_length, key_slot, created_by)
            values (@compartment, 'invoice', repeat('0', 64), '\x00', 'text/plain', 0, 1, 'alice')
            """,
            connection);
        insert.Parameters.AddWithValue("compartment", compartment);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));
        Assert.Equal("ck_documents_compartment", refused.ConstraintName);
    }
}
