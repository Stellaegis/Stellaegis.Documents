using Npgsql;

namespace Stellaegis.Documents.Tests;

/// <summary>
/// A dedicated PostgreSQL database for the store tests, its <c>doc</c> schema rebuilt from the repository's
/// DDL at every run.
/// </summary>
/// <remarks>
/// The connection string comes from <c>STELLAEGIS_DOCUMENTS_TEST_POSTGRES</c>, defaulting to a local server
/// and the database <c>stellaegis_documents_tests</c>, created if missing. When no server answers,
/// <see cref="Unavailable"/> says why, and the tests that need it are skipped rather than failed.
/// </remarks>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private const string Variable = "STELLAEGIS_DOCUMENTS_TEST_POSTGRES";
    private const string Default = "Host=127.0.0.1;Username=postgres;Database=stellaegis_documents_tests";

    /// <summary>The connection string to the test database.</summary>
    public string ConnectionString { get; } = Environment.GetEnvironmentVariable(Variable) ?? Default;

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
            }

            var ddl = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "stellaegis_documents.sql"));
            await using (var connection = new NpgsqlConnection(ConnectionString))
            {
                await connection.OpenAsync();
                await using var reset = new NpgsqlCommand("drop schema if exists doc cascade;\n" + ddl, connection);
                await reset.ExecuteNonQueryAsync();
            }
        }
        catch (NpgsqlException e)
        {
            Unavailable = $"No PostgreSQL for the store tests ({Variable} or '{Default}'): {e.Message}";
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
