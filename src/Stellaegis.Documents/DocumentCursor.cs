namespace Stellaegis.Documents;

/// <summary>
/// A position in a space's document list, ordered by <see cref="CreatedAt"/> then
/// <see cref="DocumentUniqueId"/>, both descending: a store lists the documents that come after it.
/// </summary>
/// <param name="CreatedAt">The creation instant of the last document already returned.</param>
/// <param name="DocumentUniqueId">Its public GUID, which orders documents created at the same instant.</param>
public sealed record DocumentCursor(DateTimeOffset CreatedAt, Guid DocumentUniqueId);
