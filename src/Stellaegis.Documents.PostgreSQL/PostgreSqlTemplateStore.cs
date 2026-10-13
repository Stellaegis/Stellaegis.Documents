using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Stellaegis.Database;

namespace Stellaegis.Documents.PostgreSQL;

/// <summary>
/// PostgreSQL <see cref="ITemplateStore"/> backed by <c>doc.templates</c>. The standard-or-custom
/// resolution is a single indexed query (<c>tenant_slug IN ('', @slug) ORDER BY tenant_slug DESC</c>),
/// so a tenant override transparently shadows the system default with no application branching.
/// </summary>
public sealed class PostgreSqlTemplateStore : ITemplateStore
{
    private const string Columns =
        "template_unique_id, template_code, tenant_slug, storage_path, is_customizable, " +
        "render_engine_code, status::text AS status, created_at, created_by, modified_at, modified_by";

    private readonly string _connectionString;

    static PostgreSqlTemplateStore()
        => SqlMapper.SetTypeMap(typeof(DocumentTemplate), new SnakeCaseTypeMap<DocumentTemplate>());

    /// <summary>Creates the store, validating the connection string.</summary>
    public PostgreSqlTemplateStore(IOptions<DocumentsDatabaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Value.ConnectionString))
            throw new ArgumentException($"{nameof(DocumentsDatabaseOptions)}.{nameof(DocumentsDatabaseOptions.ConnectionString)} is required.", nameof(options));

        _connectionString = options.Value.ConnectionString;
    }

    /// <inheritdoc />
    public async Task<DocumentTemplate?> ResolveAsync(string templateCode, string tenantSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        ArgumentNullException.ThrowIfNull(tenantSlug);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<DocumentTemplate>(new CommandDefinition(
            $"""
            SELECT {Columns}
            FROM doc.templates
            WHERE template_code = @templateCode
              AND tenant_slug IN ('', @tenantSlug)
              AND status = 'active'
            ORDER BY tenant_slug DESC
            LIMIT 1
            """,
            new { templateCode, tenantSlug },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DocumentTemplate?> GetAsync(string templateCode, string tenantSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        ArgumentNullException.ThrowIfNull(tenantSlug);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleOrDefaultAsync<DocumentTemplate>(new CommandDefinition(
            $"""
            SELECT {Columns}
            FROM doc.templates
            WHERE template_code = @templateCode AND tenant_slug = @tenantSlug
            LIMIT 1
            """,
            new { templateCode, tenantSlug },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CreateAsync(DocumentTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO doc.templates
              (template_unique_id, template_code, tenant_slug, storage_path, is_customizable,
               render_engine_code, status, created_at, created_by, modified_at, modified_by)
            VALUES
              (@TemplateUniqueId, @TemplateCode, @TenantSlug, @StoragePath, @IsCustomizable,
               @RenderEngineCode, @Status::doc.template_status, @CreatedAt, @CreatedBy, @ModifiedAt, @ModifiedBy)
            """,
            new
            {
                template.TemplateUniqueId,
                template.TemplateCode,
                template.TenantSlug,
                template.StoragePath,
                template.IsCustomizable,
                template.RenderEngineCode,
                Status = template.Status.ToString().ToLowerInvariant(),
                template.CreatedAt,
                template.CreatedBy,
                template.ModifiedAt,
                template.ModifiedBy,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
