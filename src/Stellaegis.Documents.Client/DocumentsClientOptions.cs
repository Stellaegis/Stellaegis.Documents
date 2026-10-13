namespace Stellaegis.Documents.Client;

/// <summary>
/// Configuration of the document API client, bound from <see cref="SectionName"/>.
/// </summary>
public sealed class DocumentsClientOptions
{
    /// <summary>Reference configuration section for these options.</summary>
    public const string SectionName = "Stellaegis:Documents:Client";

    /// <summary>
    /// Base URL under which the host maps its holder routes (e.g. <c>https://documents.example.com/api/</c>).
    /// The holder path and <c>documents</c> are appended to it.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Timeout of one request, content transfer included. Five minutes by default, since requests carry
    /// files.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}
