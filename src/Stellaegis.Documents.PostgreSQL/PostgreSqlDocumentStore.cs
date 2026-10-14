using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Documents.PostgreSQL;

/// <summary>
/// PostgreSQL <see cref="IDocumentStore"/> backed by <c>doc.documents</c> and <c>doc.document_versions</c>.
/// Every query filters on the space; a version is reached through its document, which carries it. The
/// atomic operations (create with a first version, pointer move with a new revision) run in a transaction
/// so the current pointer and the history never diverge. File names are encrypted with pgcrypto.
/// </summary>
internal sealed class PostgreSqlDocumentStore : IDocumentStore
{
    // pgp_sym_decrypt receives only the passphrase of the row's own slot: an unconfigured retired slot
    // never reaches it unless a row still uses that slot.
    private const string DecryptFileName =
        "pgp_sym_decrypt({0}.file_name, case {0}.key_slot when 1 then @Passphrase1 else @Passphrase2 end) as file_name";

    private static readonly string DocumentColumns =
        "d.document_unique_id, d.compartment, d.document_type, d.version_number, d.content_hash, " +
        string.Format(DecryptFileName, "d") + ", d.content_type, d.content_length, " +
        "d.status::text as status, d.is_versioned, d.created_at, d.created_by, d.modified_at, d.modified_by";

    private static readonly string VersionColumns =
        "v.document_unique_id, v.version_number, " +
        string.Format(DecryptFileName, "v") + ", v.content_type, v.content_length, v.content_hash, v.created_at, v.created_by";

    private readonly string _connectionString;
    private readonly int _activeKeySlot;
    private readonly string _activePassphrase;
    private readonly string _passphrase1;
    private readonly string _passphrase2;

    static PostgreSqlDocumentStore()
    {
        SnakeCaseTypeMap<DocumentRow>.Register();
        SnakeCaseTypeMap<VersionRow>.Register();
    }

    /// <summary>Creates the store, validating the connection string and the active key slot.</summary>
    public PostgreSqlDocumentStore(IOptions<DocumentsDatabaseOptions> options)
    {
        var value = Guard.NotNull(options).Value;
        _connectionString = Guard.Satisfies(value.ConnectionString, cs => !string.IsNullOrWhiteSpace(cs),
            $"{DocumentsDatabaseOptions.SectionName}:{nameof(DocumentsDatabaseOptions.ConnectionString)} is required.");
        _activeKeySlot = Guard.Satisfies(value.ActiveKeySlot, slot => slot is 1 or 2,
            $"{DocumentsDatabaseOptions.SectionName}:{nameof(DocumentsDatabaseOptions.ActiveKeySlot)} must be 1 or 2.");
        _activePassphrase = value.GetPassphrase(_activeKeySlot);
        _passphrase1 = value.GetPassphrase(1);
        _passphrase2 = value.GetPassphrase(2);
    }

    /// <inheritdoc />
    public async Task<Document?> GetAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<DocumentRow>(new CommandDefinition(
            $"select {DocumentColumns} from doc.documents d where {InSpace(space)} and d.document_unique_id = @documentUniqueId",
            WithPassphrases(new { compartment = space.Compartment?.Value, documentUniqueId }),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToDocument();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Document>> ListAsync(Space space, DocumentCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);

        // The row comparison follows the index order (created_at desc, document_unique_id desc).
        var afterPredicate = after is null
            ? string.Empty
            : "and (d.created_at, d.document_unique_id) < (@afterCreatedAt, @afterDocumentUniqueId)";

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            $"""
            select {DocumentColumns}
            from doc.documents d
            where {InSpace(space)} and d.status = 'active' {afterPredicate}
            order by d.created_at desc, d.document_unique_id desc
            limit @limit
            """,
            WithPassphrases(new { compartment = space.Compartment?.Value, afterCreatedAt = after?.CreatedAt, afterDocumentUniqueId = after?.DocumentUniqueId, limit }),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => r.ToDocument()).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Document>> ListAnyStatusAsync(Space space, int limit, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            $"select {DocumentColumns} from doc.documents d where {InSpace(space)} limit @limit",
            WithPassphrases(new { compartment = space.Compartment?.Value, limit }),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => r.ToDocument()).ToList();
    }

    /// <inheritdoc />
    public async Task CreateAsync(Document document, DocumentVersion? initialVersion, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(document);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into doc.documents
              (document_unique_id, compartment, document_type, version_number, content_hash, file_name, content_type,
               content_length, key_slot, status, is_versioned, created_at, created_by, modified_at, modified_by)
            values
              (@DocumentUniqueId, @Compartment, @DocumentType, @VersionNumber, @ContentHash,
               pgp_sym_encrypt(@FileName, @ActivePassphrase), @ContentType,
               @ContentLength, @KeySlot, @Status::doc.document_status, @IsVersioned, @CreatedAt, @CreatedBy, @ModifiedAt, @ModifiedBy)
            """,
            new
            {
                document.DocumentUniqueId,
                Compartment = document.Space.Compartment?.Value,
                document.DocumentType,
                document.VersionNumber,
                document.ContentHash,
                document.FileName,
                ActivePassphrase = _activePassphrase,
                document.ContentType,
                document.ContentLength,
                KeySlot = (short)_activeKeySlot,
                Status = StatusText(document.Status),
                document.IsVersioned,
                document.CreatedAt,
                CreatedBy = document.CreatedBy.Value,
                document.ModifiedAt,
                ModifiedBy = document.ModifiedBy?.Value,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (initialVersion is not null)
            await InsertVersionAsync(connection, transaction, initialVersion, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateCurrentAsync(Document document, DocumentVersion? newVersion, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(document);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var moved = await connection.ExecuteAsync(new CommandDefinition(
            $"""
            update doc.documents
            set version_number = @VersionNumber, content_hash = @ContentHash,
                file_name = pgp_sym_encrypt(@FileName, @ActivePassphrase), content_type = @ContentType,
                content_length = @ContentLength, key_slot = @KeySlot,
                modified_at = @ModifiedAt, modified_by = @ModifiedBy
            where {InSpace(document.Space, alias: null)} and document_unique_id = @DocumentUniqueId
            """,
            new
            {
                document.VersionNumber,
                document.ContentHash,
                document.FileName,
                ActivePassphrase = _activePassphrase,
                document.ContentType,
                document.ContentLength,
                KeySlot = (short)_activeKeySlot,
                document.ModifiedAt,
                ModifiedBy = document.ModifiedBy?.Value,
                compartment = document.Space.Compartment?.Value,
                document.DocumentUniqueId,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // No document in that space: nothing moved, and no revision may be attached to it.
        if (moved == 0)
            return;

        if (newVersion is not null)
            await InsertVersionAsync(connection, transaction, newVersion, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetStatusAsync(Space space, Guid documentUniqueId, DocumentStatus status, Actor modifiedBy, DateTimeOffset modifiedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            update doc.documents
            set status = @status::doc.document_status, modified_by = @modifiedBy, modified_at = @modifiedAt
            where {InSpace(space, alias: null)} and document_unique_id = @documentUniqueId
            """,
            new { compartment = space.Compartment?.Value, documentUniqueId, status = StatusText(status), modifiedBy = modifiedBy.Value, modifiedAt },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentVersion>> GetVersionsAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<VersionRow>(new CommandDefinition(
            $"""
            select {VersionColumns}
            from doc.document_versions v
            join doc.documents d on d.document_unique_id = v.document_unique_id
            where {InSpace(space)} and v.document_unique_id = @documentUniqueId
            order by v.version_number
            """,
            WithPassphrases(new { compartment = space.Compartment?.Value, documentUniqueId }),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => r.ToVersion()).ToList();
    }

    /// <inheritdoc />
    public async Task<DocumentVersion?> GetVersionAsync(Space space, Guid documentUniqueId, int versionNumber, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(
            $"""
            select {VersionColumns}
            from doc.document_versions v
            join doc.documents d on d.document_unique_id = v.document_unique_id
            where {InSpace(space)} and v.document_unique_id = @documentUniqueId and v.version_number = @versionNumber
            """,
            WithPassphrases(new { compartment = space.Compartment?.Value, documentUniqueId, versionNumber }),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToVersion();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Space space, Guid documentUniqueId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // The versions go with their document (on delete cascade), in the same statement.
        await connection.ExecuteAsync(new CommandDefinition(
            $"delete from doc.documents where {InSpace(space, alias: null)} and document_unique_id = @documentUniqueId",
            new { compartment = space.Compartment?.Value, documentUniqueId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private Task InsertVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DocumentVersion version, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            """
            insert into doc.document_versions
              (document_unique_id, version_number, file_name, content_type, content_length, content_hash, key_slot,
               created_at, created_by)
            values
              (@DocumentUniqueId, @VersionNumber, pgp_sym_encrypt(@FileName, @ActivePassphrase),
               @ContentType, @ContentLength, @ContentHash, @KeySlot, @CreatedAt, @CreatedBy)
            """,
            new
            {
                version.DocumentUniqueId,
                version.VersionNumber,
                version.FileName,
                ActivePassphrase = _activePassphrase,
                version.ContentType,
                version.ContentLength,
                version.ContentHash,
                KeySlot = (short)_activeKeySlot,
                version.CreatedAt,
                CreatedBy = version.CreatedBy.Value,
            },
            transaction,
            cancellationToken: cancellationToken));

    // The shared space is stored as a null compartment, which "= @compartment" never matches: each kind of
    // space gets its own predicate, and both use the index on (compartment, created_at, document_unique_id).
    private static string InSpace(Space space, string? alias = "d")
    {
        var column = alias is null ? "compartment" : $"{alias}.compartment";
        return Guard.NotNull(space).IsShared ? $"{column} is null" : $"{column} = @compartment";
    }

    private DynamicParameters WithPassphrases(object parameters)
    {
        var all = new DynamicParameters(parameters);
        all.Add("Passphrase1", _passphrase1);
        all.Add("Passphrase2", _passphrase2);
        return all;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string StatusText(DocumentStatus status) => status switch
    {
        DocumentStatus.Active => "active",
        DocumentStatus.Trashed => "trashed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown document status."),
    };

    private static DocumentStatus ParseStatus(string status) => status switch
    {
        "active" => DocumentStatus.Active,
        "trashed" => DocumentStatus.Trashed,
        _ => throw new InvalidOperationException($"Unknown document status '{status}' in doc.documents."),
    };

    private sealed class DocumentRow
    {
        public Guid DocumentUniqueId { get; set; }
        public string? Compartment { get; set; }
        public string DocumentType { get; set; } = string.Empty;
        public int VersionNumber { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long ContentLength { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool IsVersioned { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public DateTimeOffset? ModifiedAt { get; set; }
        public string? ModifiedBy { get; set; }

        public Document ToDocument() => new()
        {
            DocumentUniqueId = DocumentUniqueId,
            Space = Compartment is null ? Space.Shared : Space.Of(Primitives.Compartment.Parse(Compartment)),
            DocumentType = DocumentType,
            VersionNumber = VersionNumber,
            ContentHash = ContentHash,
            FileName = FileName,
            ContentType = ContentType,
            ContentLength = ContentLength,
            Status = ParseStatus(Status),
            IsVersioned = IsVersioned,
            CreatedAt = CreatedAt,
            CreatedBy = Actor.Parse(CreatedBy),
            ModifiedAt = ModifiedAt,
            ModifiedBy = ModifiedBy is null ? null : Actor.Parse(ModifiedBy),
        };
    }

    private sealed class VersionRow
    {
        public Guid DocumentUniqueId { get; set; }
        public int VersionNumber { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long ContentLength { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;

        public DocumentVersion ToVersion() => new()
        {
            DocumentUniqueId = DocumentUniqueId,
            VersionNumber = VersionNumber,
            FileName = FileName,
            ContentType = ContentType,
            ContentLength = ContentLength,
            ContentHash = ContentHash,
            CreatedAt = CreatedAt,
            CreatedBy = Actor.Parse(CreatedBy),
        };
    }
}
