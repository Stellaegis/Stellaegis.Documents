using Stellaegis.Primitives;

namespace Stellaegis.Documents.Client;

/// <summary>
/// The path the host maps a holder's document endpoints under, relative to
/// <see cref="DocumentsClientOptions.BaseUrl"/> — <c>t/{holder}/</c> for a host that routes holders as
/// Stellaegis routes tenants, nothing for a host without isolation.
/// </summary>
/// <param name="pathFor">Gives the path of a holder; it ends with <c>/</c> unless it is empty.</param>
public sealed class DocumentsHolderPath(Func<string, string> pathFor)
{
    /// <summary>The path for a host that maps the endpoints once, whatever the holder.</summary>
    public static DocumentsHolderPath None { get; } = new(_ => string.Empty);

    private readonly Func<string, string> _pathFor = Guard.NotNull(pathFor);

    /// <summary>The path of <paramref name="holder"/>.</summary>
    public string For(string holder) => _pathFor(holder);
}
