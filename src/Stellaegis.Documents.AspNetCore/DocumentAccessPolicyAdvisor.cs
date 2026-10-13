using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stellaegis.Documents.AspNetCore;

/// <summary>
/// Logs a startup warning when no custom <see cref="IDocumentAccessPolicy"/> is registered and the
/// permissive <see cref="AllowAllDocumentAccessPolicy"/> default is in effect — so the coarse,
/// tenant-grain access model is never a silent surprise.
/// </summary>
internal sealed class DocumentAccessPolicyAdvisor(
    IServiceProvider services,
    ILogger<DocumentAccessPolicyAdvisor> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var policy = scope.ServiceProvider.GetRequiredService<IDocumentAccessPolicy>();

        if (policy is AllowAllDocumentAccessPolicy)
        {
            logger.LogWarning(
                "No custom IDocumentAccessPolicy is registered: document access falls back to the " +
                "tenant-level user:documents.* permission only — every user holding it can access every " +
                "document of the tenant. Register an IDocumentAccessPolicy before AddStellaegisDocumentsApi " +
                "for per-document (data-driven) authorization.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
