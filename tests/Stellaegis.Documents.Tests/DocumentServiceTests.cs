using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaegis.Blobs;
using Stellaegis.MultiTenant.Context;
using Xunit;

namespace Stellaegis.Documents.Tests;

/// <summary>
/// Locks the document mechanics: SHA-256 dedup, opt-in versioning (new revision vs in-place
/// overwrite), current-pointer + history consistency, tenant isolation, and trashing.
/// </summary>
public sealed class DocumentServiceTests
{
    [Fact]
    public async Task Create_non_versioned_stores_content_in_the_tenant_ged_space_without_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, blobs, _) = Build("acme");

        var document = await service.CreateAsync("invoice", Text("hello"), "alice", cancellationToken: ct);

        document.DocumentType.Should().Be("invoice");
        document.IsVersioned.Should().BeFalse();
        document.ContentHash.Should().MatchRegex("^[0-9a-f]{64}$");
        (await service.GetVersionsAsync(document.DocumentUniqueId, ct)).Should().BeEmpty();

        (await ReadAsync(await service.OpenContentAsync(document.DocumentUniqueId, ct))).Should().Be("hello");
        blobs.LastReadLocation!.TenantSlug.Should().Be("acme");
        blobs.LastReadLocation.Name.Should().Be(BlobNames.Documents);
    }

    [Fact]
    public async Task Create_versioned_records_version_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, _, _) = Build("acme");

        var document = await service.CreateAsync("contract", Text("v1"), "alice", isVersioned: true, cancellationToken: ct);

        (await service.GetVersionsAsync(document.DocumentUniqueId, ct)).Should().ContainSingle()
            .Which.VersionNumber.Should().Be(1);
        (await ReadAsync(await service.OpenVersionContentAsync(document.DocumentUniqueId, 1, ct))).Should().Be("v1");
    }

    [Fact]
    public async Task Update_with_identical_content_is_a_noop()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, _, _) = Build("acme");
        var document = await service.CreateAsync("invoice", Text("same"), "alice", isVersioned: true, cancellationToken: ct);

        var after = await service.UpdateContentAsync(document.DocumentUniqueId, Text("same"), "bob", ct);

        after.ContentHash.Should().Be(document.ContentHash);
        after.ModifiedAt.Should().BeNull();
        (await service.GetVersionsAsync(document.DocumentUniqueId, ct)).Should().ContainSingle(); // still v1 only
    }

    [Fact]
    public async Task Update_of_a_versioned_document_appends_a_revision_and_preserves_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, _, _) = Build("acme");
        var document = await service.CreateAsync("contract", Text("v1"), "alice", isVersioned: true, cancellationToken: ct);

        var updated = await service.UpdateContentAsync(document.DocumentUniqueId, Text("v2"), "bob", ct);

        updated.StoragePath.Should().NotBe(document.StoragePath);
        (await service.GetVersionsAsync(document.DocumentUniqueId, ct))
            .Select(v => v.VersionNumber).Should().Equal(1, 2);
        (await ReadAsync(await service.OpenVersionContentAsync(document.DocumentUniqueId, 1, ct))).Should().Be("v1");
        (await ReadAsync(await service.OpenContentAsync(document.DocumentUniqueId, ct))).Should().Be("v2");
    }

    [Fact]
    public async Task Update_of_a_non_versioned_document_overwrites_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, _, _) = Build("acme");
        var document = await service.CreateAsync("note", Text("old"), "alice", cancellationToken: ct);

        var updated = await service.UpdateContentAsync(document.DocumentUniqueId, Text("new"), "bob", ct);

        updated.StoragePath.Should().Be(document.StoragePath); // same key, overwritten
        updated.ContentHash.Should().NotBe(document.ContentHash);
        (await service.GetVersionsAsync(document.DocumentUniqueId, ct)).Should().BeEmpty();
        (await ReadAsync(await service.OpenContentAsync(document.DocumentUniqueId, ct))).Should().Be("new");
    }

    [Fact]
    public async Task BusinessObjectLink_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, _, _) = Build("acme");
        var link = Guid.NewGuid();

        var document = await service.CreateAsync("invoice", Text("x"), "alice", businessObjectLink: link, cancellationToken: ct);

        (await service.GetAsync(document.DocumentUniqueId, ct))!.BusinessObjectLink.Should().Be(link);
    }

    [Fact]
    public async Task Documents_are_isolated_per_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, _, tenant) = Build("acme");
        var document = await service.CreateAsync("invoice", Text("x"), "alice", cancellationToken: ct);

        tenant.SetTenant("globex");

        (await service.GetAsync(document.DocumentUniqueId, ct)).Should().BeNull();
        Func<Task> act = () => service.OpenContentAsync(document.DocumentUniqueId, ct);
        await act.Should().ThrowAsync<DocumentNotFoundException>();
    }

    [Fact]
    public async Task Trashing_sets_status_and_excludes_from_listing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, _, _) = Build("acme");
        var document = await service.CreateAsync("invoice", Text("x"), "alice", cancellationToken: ct);

        await service.TrashAsync(document.DocumentUniqueId, "bob", ct);

        (await service.GetAsync(document.DocumentUniqueId, ct))!.Status.Should().Be(DocumentStatus.Trashed);
        (await service.ListAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Update_of_a_missing_document_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, _, _) = Build("acme");

        Func<Task> act = () => service.UpdateContentAsync(Guid.NewGuid(), Text("x"), "bob", ct);

        await act.Should().ThrowAsync<DocumentNotFoundException>();
    }

    [Fact]
    public async Task Create_without_a_tenant_context_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, _, tenant) = Build("acme");
        tenant.Clear();

        Func<Task> act = () => service.CreateAsync("invoice", Text("x"), "alice", cancellationToken: ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Helpers ──

    private static (IDocumentService Service, RecordingBlobStore Blobs, FakeTenantContext Tenant) Build(string tenantSlug)
    {
        var tenant = new FakeTenantContext(tenantSlug);
        var blobs = new RecordingBlobStore();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IBlobStore>(blobs);
        services.AddSingleton<ITenantContext>(tenant);
        services.AddStellaegisDocumentsInMemory();

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IDocumentService>(), blobs, tenant);
    }

    private static MemoryStream Text(string content) => new(Encoding.UTF8.GetBytes(content));

    private static async Task<string> ReadAsync(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
