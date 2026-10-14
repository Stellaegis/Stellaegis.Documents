namespace Stellaegis.Documents.Client;

/// <summary>
/// Configuration of the document API client, bound from <see cref="SectionName"/>.
/// </summary>
/// <remarks>
/// The host maps the document endpoints where it chooses: a group per compartment, and a group for the shared
/// space. Each URL names one of them, and neither is deduced from the other. At least one is required.
/// </remarks>
public sealed class DocumentsClientOptions
{
    /// <summary>Reference configuration section for these options.</summary>
    public const string SectionName = "Stellaegis:Documents:Client";

    /// <summary>The placeholder <see cref="CompartmentUrl"/> carries for the compartment of each call.</summary>
    public const string CompartmentPlaceholder = "{compartment}";

    /// <summary>
    /// Absolute URL under which the host maps the endpoints of a compartment, <c>documents</c> appended to it
    /// (e.g. <c>https://documents.example.com/api/orgs/{compartment}/</c>). <see cref="CompartmentPlaceholder"/>
    /// is replaced by the compartment of each call; without it, the host resolves the compartment some other
    /// way, such as from the token. <see langword="null"/>: the client reaches no compartment.
    /// </summary>
    public string? CompartmentUrl { get; init; }

    /// <summary>
    /// Absolute URL under which the host maps the endpoints of the shared space, <c>documents</c> appended to it
    /// (e.g. <c>https://documents.example.com/api/shared/</c>). <see langword="null"/>: the client reaches no
    /// shared document.
    /// </summary>
    public string? SharedUrl { get; init; }

    /// <summary>
    /// Timeout of one request, content transfer included. Five minutes by default, since requests carry
    /// files.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}
