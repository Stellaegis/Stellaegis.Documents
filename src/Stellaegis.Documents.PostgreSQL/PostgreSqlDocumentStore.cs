using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Stellaegis.Database;

namespace Stellaegis.Documents.PostgreSQL;

/// <summary>
/// PostgreSQL <see cref="IDocumentStore"/> backed by <c>doc.documents</c> and
/// <c>doc.document_versions</c>. The atomic operations (create-with-first-version, pointer-move +
/// revision) run inside a transaction so the current pointer and the history never diverge.
/// </summary>
public sealed class PostgreSqlDocumentStore : IDocumentStore
{
    private const string DocumentColumns =
        "document_unique_id, tenant_slug, document_type, storage_path, content_hash, business_object_link, " +
        "status::text AS status, is_versioned, created_at, created_by, modified_at, modified_by";

    private const string VersionColumns =
        "document_unique_id, version_number, storage_path, created_at, created_by";

    private readonly string _connectionString;

    static PostgreSqlDocumentStore()
    {
        SqlMapper.SetTypeMap(typeof(Document), new SnakeCaseTypeMap<Document>());
        SqlMapper.SetTypeMap(typeof(DocumentVersion), new SnakeCaseTypeMap<DocumentVersion>());
    }

    /// <summary>Creates the store, validating the connection string.</summary>
    public PostgreSqlDocumentStore(IOptions<DocumentsDatabaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Value.ConnectionString))
            throw new ArgumentException($"{nameof(DocumentsDatabaseOptions)}.{nameof(DocumentsDatabaseOptions.ConnectionString)} is required.", nameof(options));

        _connectionString = options.Value.ConnectionString;
    }

    /// <inheritdoc />
    public async Task<Document?> GetAsync(Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<Document>(new CommandDefinition(
            $"SELECT {DocumentColumns} FROM doc.documents WHERE document_unique_id = @documentUniqueId",
            new { documentUniqueId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Document>> ListByTenantAsync(string tenantSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSlug);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Document>(new CommandDefinition(
            $"SELECT {DocumentColumns} FROM doc.documents WHERE tenant_slug = @tenantSlug AND status = 'active' ORDER BY created_at DESC",
            new { tenantSlug },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task CreateAsync(Document document, DocumentVersion? initialVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (initialVersion is null)
        {
            await connection.ExecuteAsync(InsertDocumentCommand(document, null, cancellationToken)).ConfigureAwait(false);
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(InsertDocumentCommand(document, transaction, cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(InsertVersionCommand(initialVersion, transaction, cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateCurrentAsync(Document document, DocumentVersion? newVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (newVersion is null)
        {
            await connection.ExecuteAsync(UpdatePointerCommand(document, null, cancellationToken)).ConfigureAwait(false);
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(UpdatePointerCommand(document, transaction, cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(InsertVersionCommand(newVersion, transaction, cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetStatusAsync(Guid documentUniqueId, DocumentStatus status, string modifiedBy, DateTimeOffset modifiedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE doc.documents
            SET status = @Status::doc.document_status, modified_by = @ModifiedBy, modified_at = @ModifiedAt
            WHERE document_unique_id = @DocumentUniqueId
            """,
            new { DocumentUniqueId = documentUniqueId, Status = status.ToString().ToLowerInvariant(), ModifiedBy = modifiedBy, ModifiedAt = modifiedAt },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetMaxVersionNumberAsync(Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(version_number), 0) FROM doc.document_versions WHERE document_unique_id = @documentUniqueId",
            new { documentUniqueId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<DocumentVersion>(new CommandDefinition(
            $"SELECT {VersionColumns} FROM doc.document_versions WHERE document_unique_id = @documentUniqueId ORDER BY version_number",
            new { documentUniqueId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<DocumentVersion?> GetVersionAsync(Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<DocumentVersion>(new CommandDefinition(
            $"SELECT {VersionColumns} FROM doc.document_versions WHERE document_unique_id = @documentUniqueId AND version_number = @versionNumber",
            new { documentUniqueId, versionNumber },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static CommandDefinition InsertDocumentCommand(Document document, NpgsqlTransaction? transaction, CancellationToken cancellationToken) => new(
        """
        INSERT INTO doc.documents
          (document_unique_id, tenant_slug, document_type, storage_path, content_hash, business_object_link,
           status, is_versioned, created_at, created_by, modified_at, modified_by)
        VALUES
          (@DocumentUniqueId, @TenantSlug, @DocumentType, @StoragePath, @ContentHash, @BusinessObjectLink,
           @Status::doc.document_status, @IsVersioned, @CreatedAt, @CreatedBy, @ModifiedAt, @ModifiedBy)
        """,
        new
        {
            document.DocumentUniqueId,
            document.TenantSlug,
            document.DocumentType,
            document.StoragePath,
            document.ContentHash,
            document.BusinessObjectLink,
            Status = document.Status.ToString().ToLowerInvariant(),
            document.IsVersioned,
            document.CreatedAt,
            document.CreatedBy,
            document.ModifiedAt,
            document.ModifiedBy,
        },
        transaction,
        cancellationToken: cancellationToken);

    private static CommandDefinition UpdatePointerCommand(Document document, NpgsqlTransaction? transaction, CancellationToken cancellationToken) => new(
        """
        UPDATE doc.documents
        SET storage_path = @StoragePath, content_hash = @ContentHash, modified_at = @ModifiedAt, modified_by = @ModifiedBy
        WHERE document_unique_id = @DocumentUniqueId
        """,
        new { document.StoragePath, document.ContentHash, document.ModifiedAt, document.ModifiedBy, document.DocumentUniqueId },
        transaction,
        cancellationToken: cancellationToken);

    private static CommandDefinition InsertVersionCommand(DocumentVersion version, NpgsqlTransaction? transaction, CancellationToken cancellationToken) => new(
        """
        INSERT INTO doc.document_versions (document_unique_id, version_number, storage_path, created_at, created_by)
        VALUES (@DocumentUniqueId, @VersionNumber, @StoragePath, @CreatedAt, @CreatedBy)
        """,
        new { version.DocumentUniqueId, version.VersionNumber, version.StoragePath, version.CreatedAt, version.CreatedBy },
        transaction,
        cancellationToken: cancellationToken);

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
