namespace Stellaegis.Documents;

/// <summary>
/// One page of a space's active documents, most recent first.
/// </summary>
/// <param name="Documents">The documents of this page.</param>
/// <param name="ContinuationToken">
/// The opaque token that asks for the next page, or <see langword="null"/> when this page is the last.
/// </param>
public sealed record DocumentPage(IReadOnlyList<Document> Documents, string? ContinuationToken);
