using Npgsql;

namespace Stellaegis.Documents.Tests;

/// <summary>
/// A dedicated PostgreSQL database for the store tests, its <c>doc</c> schema rebuilt from the repository's
/// DDL at every run, without the isolation by owner.
/// </summary>
/// <remarks>
/// The connection string comes from <c>STELLAEGIS_DOCUMENTS_TEST_POSTGRES</c>, defaulting to a local server
/// and the database <c>stellaegis_documents_tests</c>, created if missing. When no server answers,
/// <see cref="PostgreSqlDatabase.Unavailable"/> says why, and the tests that need it are skipped rather than failed.
/// </remarks>
public sealed class PostgreSqlFixture() : PostgreSqlDatabase(databaseSuffix: null, ownerIsolation: false);

/// <summary>
/// A second database, the name of the first suffixed with <c>_owner</c>, where <c>owner-isolation.sql</c> is
/// applied after the DDL: each actor sees only the documents it created.
/// </summary>
public sealed class OwnerIsolationFixture() : PostgreSqlDatabase(databaseSuffix: "_owner", ownerIsolation: true);

/// <summary>A test database, rebuilt at every run, and the ordinary role that row-level security applies to.</summary>
public abstract class PostgreSqlDatabase : IAsyncLifetime
{
    private const string Variable = "STELLAEGIS_DOCUMENTS_TEST_POSTGRES";
    private const string Default = "Host=127.0.0.1;Username=postgres;Database=stellaegis_documents_tests";

    // A role cluster-wide, created once and kept: a login without superuser rights, which the policies apply to.
    private const string AppRole = "stellaegis_documents_app";

    private readonly bool _ownerIsolation;

    private protected PostgreSqlDatabase(string? databaseSuffix, bool ownerIsolation)
    {
        var configured = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable) ?? Default);
        configured.Database += databaseSuffix;
        ConnectionString = configured.ConnectionString;
        AppConnectionString = new NpgsqlConnectionStringBuilder(ConnectionString) { Username = AppRole, Password = AppRole }.ConnectionString;
        _ownerIsolation = ownerIsolation;
    }

    /// <summary>
    /// The connection string to the test database, under the configured role — a superuser locally, which row-level
    /// security never applies to.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>The connection string to the test database under an ordinary role, which row-level security applies to.</summary>
    public string AppConnectionString { get; }

    /// <summary>Why the database cannot be used, or <c>null</c> when it can.</summary>
    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var target = new NpgsqlConnectionStringBuilder(ConnectionString);
        var server = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };

        try
        {
            await using (var connection = new NpgsqlConnection(server.ConnectionString))
            {
                await connection.OpenAsync();
                await using var exists = new NpgsqlCommand("select 1 from pg_database where datname = @name", connection);
                exists.Parameters.AddWithValue("name", target.Database!);
                if (await exists.ExecuteScalarAsync() is null)
                {
                    await using var create = new NpgsqlCommand($"create database \"{target.Database}\"", connection);
                    await create.ExecuteNonQueryAsync();
                }

                // Two fixtures may create it at once: the second finds it there.
                await using var role = new NpgsqlCommand(
                    $"""
                    do $$ begin
                      create role {AppRole} login password '{AppRole}';
                    exception when duplicate_object or unique_violation then null;
                    end $$;
                    """, connection);
                await role.ExecuteNonQueryAsync();
            }

            var script = await ReadSqlAsync("stellaegis_documents.sql");
            if (_ownerIsolation)
                script += "\n" + await ReadSqlAsync("owner-isolation.sql");

            await using (var connection = new NpgsqlConnection(ConnectionString))
            {
                await connection.OpenAsync();
                await using var reset = new NpgsqlCommand(
                    $"""
                    drop schema if exists doc cascade;
                    {script}
                    grant usage on schema doc to {AppRole};
                    grant select, insert, update, delete on all tables in schema doc to {AppRole};
                    """, connection);
                await reset.ExecuteNonQueryAsync();
            }
        }
        catch (NpgsqlException e)
        {
            Unavailable = $"No PostgreSQL for the store tests ({Variable} or '{Default}'): {e.Message}";
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Task<string> ReadSqlAsync(string name) => File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", name));
}
