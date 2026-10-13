using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaegis.Blobs;
using Stellaegis.Common;
using Stellaegis.MultiTenant.Context;
using Xunit;

namespace Stellaegis.Documents.Tests;

/// <summary>
/// Locks the template resolution model: standard-or-custom sentinel, status filtering,
/// and — through <see cref="IDocumentTemplateService"/> — the propagation of the resolved
/// tenant slug to the blob space.
/// </summary>
public sealed class TemplateResolutionTests
{
    // ── Store: ResolveAsync (standard-or-custom, active only) ──

    [Fact]
    public async Task Resolve_returns_system_default_when_no_tenant_override()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(out _, ambientSlug: null);
        await store.CreateAsync(Tpl("invoice", "", "system/invoice.scriban"), ct);

        var resolved = await store.ResolveAsync("invoice", "acme", ct);

        resolved.Should().NotBeNull();
        resolved!.TenantSlug.Should().BeEmpty();
        resolved.StoragePath.Should().Be("system/invoice.scriban");
    }

    [Fact]
    public async Task Resolve_prefers_tenant_override_over_system_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(out _, ambientSlug: null);
        await store.CreateAsync(Tpl("invoice", "", "system/invoice.scriban"), ct);
        await store.CreateAsync(Tpl("invoice", "acme", "acme/invoice.scriban"), ct);

        (await store.ResolveAsync("invoice", "acme", ct))!.TenantSlug.Should().Be("acme");
        // A different tenant with no override still gets the system default.
        (await store.ResolveAsync("invoice", "globex", ct))!.TenantSlug.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_excludes_deprecated_override_and_falls_back_to_system()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(out _, ambientSlug: null);
        await store.CreateAsync(Tpl("invoice", "", "system/invoice.scriban"), ct);
        await store.CreateAsync(Tpl("invoice", "acme", "acme/invoice.scriban", TemplateStatus.Deprecated), ct);

        var resolved = await store.ResolveAsync("invoice", "acme", ct);

        resolved!.TenantSlug.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_returns_null_when_only_non_active_rows_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(out _, ambientSlug: null);
        await store.CreateAsync(Tpl("invoice", "", "system/invoice.scriban", TemplateStatus.Archived), ct);

        (await store.ResolveAsync("invoice", "acme", ct)).Should().BeNull();
    }

    [Fact]
    public async Task Resolve_returns_null_when_no_row_matches()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(out _, ambientSlug: null);

        (await store.ResolveAsync("unknown", "acme", ct)).Should().BeNull();
    }

    // ── Store: GetAsync (exact, any status, no fallback) ──

    [Fact]
    public async Task Get_returns_exact_row_regardless_of_status_and_without_fallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(out _, ambientSlug: null);
        await store.CreateAsync(Tpl("invoice", "acme", "acme/invoice.scriban", TemplateStatus.Deprecated), ct);

        var exact = await store.GetAsync("invoice", "acme", ct);
        exact.Should().NotBeNull();
        exact!.Status.Should().Be(TemplateStatus.Deprecated);

        // No standard-or-custom fallback: the system row is not synthesised from a tenant override.
        (await store.GetAsync("invoice", "", ct)).Should().BeNull();
    }

    // ── Service: blob-space selection follows the resolved tenant slug ──

    [Fact]
    public async Task OpenContent_reads_system_blob_space_for_system_resolution()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(out var sp, ambientSlug: "acme");
        var blobs = (RecordingBlobStore)sp.GetRequiredService<IBlobStore>();
        await store.CreateAsync(Tpl("invoice", "", "system/invoice.scriban"), ct);
        blobs.Put(BlobLocation.System(BlobNames.Templates, "system/invoice.scriban"), "SYSTEM BODY");

        using var scope = sp.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IDocumentTemplateService>();
        await using var stream = await service.OpenContentAsync("invoice", ct);

        (await ReadAsync(stream)).Should().Be("SYSTEM BODY");
        blobs.LastReadLocation.Should().NotBeNull();
        blobs.LastReadLocation!.IsSystem.Should().BeTrue();
        blobs.LastReadLocation.Name.Should().Be(BlobNames.Templates);
        blobs.LastReadLocation.Key.Should().Be("system/invoice.scriban");
    }

    [Fact]
    public async Task OpenContent_reads_tenant_blob_space_for_tenant_override()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(out var sp, ambientSlug: "acme");
        var blobs = (RecordingBlobStore)sp.GetRequiredService<IBlobStore>();
        await store.CreateAsync(Tpl("invoice", "", "system/invoice.scriban"), ct);
        await store.CreateAsync(Tpl("invoice", "acme", "acme/invoice.scriban"), ct);
        blobs.Put(new BlobLocation("acme", BlobNames.Templates, "acme/invoice.scriban"), "ACME BODY");

        using var scope = sp.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IDocumentTemplateService>();
        await using var stream = await service.OpenContentAsync("invoice", ct);

        (await ReadAsync(stream)).Should().Be("ACME BODY");
        blobs.LastReadLocation!.TenantSlug.Should().Be("acme");
    }

    [Fact]
    public async Task OpenContent_throws_TemplateNotFound_when_nothing_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(out var sp, ambientSlug: "acme");

        using var scope = sp.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IDocumentTemplateService>();
        Func<Task> act = () => service.OpenContentAsync("nope", ct);

        await act.Should().ThrowAsync<TemplateNotFoundException>();
    }

    [Fact]
    public async Task Resolve_ambient_throws_when_no_tenant_context()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(out var sp, ambientSlug: null);

        using var scope = sp.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IDocumentTemplateService>();
        Func<Task> act = () => service.ResolveAsync("invoice", ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Seeder: idempotent system seeding from DI-provided definitions ──

    [Fact]
    public async Task SystemTemplateSeeder_seeds_definitions_idempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(out var sp, ambientSlug: null,
            new SystemTemplateDefinition { TemplateCode = "invoice", StoragePath = "system/invoice.scriban" });

        await RunSeedersAsync(sp, ct);
        await RunSeedersAsync(sp, ct); // second run must neither throw nor duplicate

        var row = await store.GetAsync("invoice", "", ct);
        row.Should().NotBeNull();
        row!.TenantSlug.Should().BeEmpty();
        row.StoragePath.Should().Be("system/invoice.scriban");
    }

    // ── Helpers ──

    private static ITemplateStore Store(out ServiceProvider provider, string? ambientSlug, params SystemTemplateDefinition[] systemTemplates)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IBlobStore>(new RecordingBlobStore());
        services.AddSingleton<ITenantContext>(new FakeTenantContext(ambientSlug));
        services.AddStellaegisDocumentsInMemory();
        foreach (var template in systemTemplates)
            services.AddStellaegisSystemTemplate(template);

        provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ITemplateStore>();
    }

    private static async Task RunSeedersAsync(ServiceProvider provider, CancellationToken ct)
    {
        using var scope = provider.CreateScope();
        foreach (var seeder in scope.ServiceProvider.GetServices<IDataSeeder>())
            await seeder.SeedAsync(ct);
    }

    private static DocumentTemplate Tpl(string code, string slug, string path, TemplateStatus status = TemplateStatus.Active)
        => new()
        {
            TemplateUniqueId = Guid.NewGuid(),
            TemplateCode = code,
            TenantSlug = slug,
            StoragePath = path,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        };

    private static async Task<string> ReadAsync(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
