using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaegis.Backplane;
using Stellaegis.Backplane.DocumentEvents;
using Stellaegis.Blobs;
using Stellaegis.MultiTenant.Context;
using Xunit;

namespace Stellaegis.Documents.Tests;

/// <summary>
/// Locks the GED backplane contract: a document indexing event on create/content-change, a trashed
/// event on trash, and no superfluous event when a re-upload is identical (dedup).
/// </summary>
public sealed class DocumentBackplaneTests
{
    [Fact]
    public async Task Create_publishes_a_document_indexed_event_with_system_metadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, backplane) = Build("acme");
        var link = Guid.NewGuid();

        var document = await service.CreateAsync("invoice", Text("hello"), "alice", businessObjectLink: link, isVersioned: true, cancellationToken: ct);

        var indexed = backplane.Published.OfType<DocumentIndexedEvent>().Should().ContainSingle().Subject;
        indexed.TenantSlug.Should().Be("acme");
        indexed.TenantScope.Should().Be("acme");
        indexed.DocumentId.Should().Be(document.DocumentUniqueId.ToString("N"));
        indexed.DocumentType.Should().Be("invoice");
        indexed.ContentHash.Should().Be(document.ContentHash);
        indexed.BusinessObjectLink.Should().Be(link.ToString("N"));
        indexed.IsVersioned.Should().BeTrue();
    }

    [Fact]
    public async Task Update_with_changed_content_publishes_a_second_indexed_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, backplane) = Build("acme");
        var document = await service.CreateAsync("note", Text("old"), "alice", cancellationToken: ct);

        await service.UpdateContentAsync(document.DocumentUniqueId, Text("new"), "bob", ct);

        backplane.Published.OfType<DocumentIndexedEvent>().Should().HaveCount(2);
    }

    [Fact]
    public async Task Update_with_identical_content_publishes_no_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, backplane) = Build("acme");
        var document = await service.CreateAsync("note", Text("same"), "alice", cancellationToken: ct);
        backplane.Published.Clear();

        await service.UpdateContentAsync(document.DocumentUniqueId, Text("same"), "bob", ct);

        backplane.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Trash_publishes_a_document_trashed_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, backplane) = Build("acme");
        var document = await service.CreateAsync("invoice", Text("x"), "alice", cancellationToken: ct);
        backplane.Published.Clear();

        await service.TrashAsync(document.DocumentUniqueId, "bob", ct);

        var trashed = backplane.Published.OfType<DocumentTrashedEvent>().Should().ContainSingle().Subject;
        trashed.TenantSlug.Should().Be("acme");
        trashed.DocumentId.Should().Be(document.DocumentUniqueId.ToString("N"));
    }

    // ── Helpers ──

    private static (IDocumentService Service, RecordingBackplane Backplane) Build(string tenantSlug)
    {
        var backplane = new RecordingBackplane();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IBlobStore>(new RecordingBlobStore());
        services.AddSingleton<ITenantContext>(new FakeTenantContext(tenantSlug));
        services.AddSingleton<IBackplane>(backplane);
        services.AddStellaegisDocumentsInMemory();

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IDocumentService>(), backplane);
    }

    private static MemoryStream Text(string content) => new(Encoding.UTF8.GetBytes(content));
}
