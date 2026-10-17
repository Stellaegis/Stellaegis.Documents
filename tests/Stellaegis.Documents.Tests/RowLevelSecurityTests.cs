using Npgsql;
using Stellaegis.Primitives;
using static Stellaegis.Documents.Tests.Content;

namespace Stellaegis.Documents.Tests;

/// <summary>
/// The row-level security of the <c>doc</c> schema, under an ordinary role: a session sees the rows of its space
/// only, whatever the query. The documents are created through the service, which sets the space of each
/// connection; the checks read the tables directly, with a query that filters on nothing but the documents of the
/// test.
/// </summary>
public sealed class RowLevelSecurityTests(PostgreSqlFixture database) : IClassFixture<PostgreSqlFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DocumentsHarness Harness()
    {
        Assert.SkipWhen(database.Unavailable is not null, database.Unavailable ?? string.Empty);
        return DocumentsHarness.PostgreSql(database.AppConnectionString);
    }

    [Fact]
    public async Task The_service_works_under_the_policies()
    {
        await using var harness = Harness();
        var compartment = NewCompartment();

        var document = await harness.Documents.CreateAsync(compartment, "contract", Upload("c1"), Alice, isVersioned: true, cancellationToken: Ct);
        await harness.Documents.UpdateContentAsync(compartment, document.DocumentUniqueId, Upload("c2"), Alice, Ct);
        await harness.Documents.CreateAsync(Space.Shared, "template", Upload("t"), Alice, cancellationToken: Ct);

        Assert.Equal(2, (await harness.Documents.GetVersionsAsync(compartment, document.DocumentUniqueId, Alice, Ct)).Count);
        Assert.Single((await harness.Documents.ListAsync(compartment, Alice, cancellationToken: Ct)).Documents);
        Assert.Equal(1, await harness.Documents.PurgeCompartmentAsync(compartment, Purger, Ct));
    }

    [Fact]
    public async Task A_session_sees_only_the_documents_and_versions_of_its_space()
    {
        await using var harness = Harness();
        var acme = NewCompartment();
        var globex = NewCompartment();
        var inAcme = await harness.Documents.CreateAsync(acme, "contract", Upload("a"), Alice, isVersioned: true, cancellationToken: Ct);
        var inGlobex = await harness.Documents.CreateAsync(globex, "contract", Upload("g"), Alice, isVersioned: true, cancellationToken: Ct);
        var shared = await harness.Documents.CreateAsync(Space.Shared, "template", Upload("s"), Alice, isVersioned: true, cancellationToken: Ct);
        Guid[] all = [inAcme.DocumentUniqueId, inGlobex.DocumentUniqueId, shared.DocumentUniqueId];

        Assert.Equal([inAcme.DocumentUniqueId], await VisibleAsync(acme.Value, "documents", all));
        Assert.Equal([inAcme.DocumentUniqueId], await VisibleAsync(acme.Value, "document_versions", all));
        Assert.Equal([shared.DocumentUniqueId], await VisibleAsync(Space.SharedSegment, "documents", all));
        Assert.Equal([shared.DocumentUniqueId], await VisibleAsync(Space.SharedSegment, "document_versions", all));
    }

    [Fact]
    public async Task A_session_without_a_space_sees_nothing()
    {
        await using var harness = Harness();
        var document = await harness.Documents.CreateAsync(NewCompartment(), "contract", Upload("a"), Alice, cancellationToken: Ct);
        var shared = await harness.Documents.CreateAsync(Space.Shared, "template", Upload("s"), Alice, cancellationToken: Ct);

        Assert.Empty(await VisibleAsync(space: null, "documents", [document.DocumentUniqueId, shared.DocumentUniqueId]));
    }

    [Fact]
    public async Task A_session_cannot_write_a_document_into_another_space()
    {
        Assert.SkipWhen(database.Unavailable is not null, database.Unavailable ?? string.Empty);
        await using var connection = await OpenAsync(NewCompartment().Value);
        await using var insert = new NpgsqlCommand(
            """
            insert into doc.documents (compartment, document_type, content_hash, file_name, content_type, content_length, key_slot, created_by)
            values (@compartment, 'note', repeat('0', 64), '\x00', 'text/plain', 0, 1, 'alice')
            """, connection);
        insert.Parameters.AddWithValue("compartment", NewCompartment().Value);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task Without_the_owner_isolation_two_actors_of_a_compartment_see_each_other()
    {
        await using var harness = Harness();
        var compartment = NewCompartment();
        var document = await harness.Documents.CreateAsync(compartment, "note", Upload("a"), Alice, cancellationToken: Ct);

        Assert.NotNull(await harness.Documents.GetAsync(compartment, document.DocumentUniqueId, Bob, Ct));
    }

    // The documents of ids a session in space can read from table, under the ordinary role. Without a space, the
    // session sets nothing.
    private async Task<List<Guid>> VisibleAsync(string? space, string table, Guid[] ids)
    {
        await using var connection = await OpenAsync(space);
        await using var query = new NpgsqlCommand(
            $"select distinct document_unique_id from doc.{table} where document_unique_id = any(@ids)", connection);
        query.Parameters.AddWithValue("ids", ids);
        var visible = new List<Guid>();
        await using var reader = await query.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            visible.Add(reader.GetGuid(0));
        return visible;
    }

    private async Task<NpgsqlConnection> OpenAsync(string? space)
    {
        var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync(Ct);
        if (space is not null)
        {
            await using var context = new NpgsqlCommand(
                "select set_config('stellaegis.space', @space, false), set_config('stellaegis.actor', 'alice', false)", connection);
            context.Parameters.AddWithValue("space", space);
            await context.ExecuteNonQueryAsync(Ct);
        }
        return connection;
    }
}

/// <summary>
/// <c>owner-isolation.sql</c> applied: within a compartment, each actor sees only the documents it created, and the
/// purge still empties the compartment.
/// </summary>
public sealed class OwnerIsolationTests(OwnerIsolationFixture database) : IClassFixture<OwnerIsolationFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DocumentsHarness Harness()
    {
        Assert.SkipWhen(database.Unavailable is not null, database.Unavailable ?? string.Empty);
        return DocumentsHarness.PostgreSql(database.AppConnectionString);
    }

    [Fact]
    public async Task An_actor_does_not_see_the_documents_of_another()
    {
        await using var harness = Harness();
        var compartment = NewCompartment();
        var alices = await harness.Documents.CreateAsync(compartment, "note", Upload("a"), Alice, isVersioned: true, cancellationToken: Ct);
        var bobs = await harness.Documents.CreateAsync(compartment, "note", Upload("b"), Bob, cancellationToken: Ct);

        Assert.Null(await harness.Documents.GetAsync(compartment, alices.DocumentUniqueId, Bob, Ct));
        Assert.Equal([bobs.DocumentUniqueId], (await harness.Documents.ListAsync(compartment, Bob, cancellationToken: Ct)).Documents.Select(d => d.DocumentUniqueId));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.GetVersionsAsync(compartment, alices.DocumentUniqueId, Bob, Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => harness.Documents.TrashAsync(compartment, alices.DocumentUniqueId, Bob, Ct));
        Assert.NotNull(await harness.Documents.GetAsync(compartment, alices.DocumentUniqueId, Alice, Ct));
    }

    [Fact]
    public async Task The_purge_empties_the_compartment_whoever_created_the_documents()
    {
        await using var harness = Harness();
        var compartment = NewCompartment();
        await harness.Documents.CreateAsync(compartment, "note", Upload("a"), Alice, isVersioned: true, cancellationToken: Ct);
        await harness.Documents.CreateAsync(compartment, "note", Upload("b"), Bob, cancellationToken: Ct);

        Assert.Equal(2, await harness.Documents.PurgeCompartmentAsync(compartment, Purger, Ct));
        Assert.Empty(harness.BlobNames());
    }
}
