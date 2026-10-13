namespace Stellaegis.Documents.PostgreSQL;

/// <summary>
/// Options for the PostgreSQL document store (schema <c>doc</c>).
/// </summary>
/// <remarks>
/// File names are encrypted with pgcrypto under one of two passphrase slots, which alternate for a key
/// rotation: new writes use <see cref="ActiveKeySlot"/>, and each row records the slot that encrypted it.
/// Rows keep their slot until they are written again, so a retired passphrase stays configured as long
/// as a row uses it.
/// <code>
/// Stellaegis:Documents:PostgreSQL:ConnectionString = Host=…;Database=…
/// Stellaegis:Documents:PostgreSQL:ActiveKeySlot    = 1
/// Stellaegis:Documents:PostgreSQL:Keys:1           = passphrase-for-slot-1
/// Stellaegis:Documents:PostgreSQL:Keys:2           = (empty until the first rotation)
/// </code>
/// </remarks>
public sealed class DocumentsDatabaseOptions
{
    /// <summary>Reference configuration section for these options.</summary>
    public const string SectionName = "Stellaegis:Documents:PostgreSQL";

    /// <summary>Connection string to the database hosting the <c>doc</c> schema.</summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Passphrases by slot (1 or 2). Supply them from a secret store or the environment, never from a
    /// committed file.
    /// </summary>
    public Dictionary<int, string> Keys { get; init; } = [];

    /// <summary>The slot whose passphrase encrypts new writes (1 or 2).</summary>
    public int ActiveKeySlot { get; init; } = 1;

    /// <summary>
    /// The passphrase of <paramref name="slot"/>: the active slot must have one; another slot may be empty
    /// as long as no row uses it, since the reading query only applies a row's own slot.
    /// </summary>
    internal string GetPassphrase(int slot)
    {
        if (Keys.TryGetValue(slot, out var key) && !string.IsNullOrWhiteSpace(key))
            return key;

        if (slot != ActiveKeySlot)
            return string.Empty;

        throw new InvalidOperationException(
            $"No passphrase configured for the active key slot {slot}. Set {SectionName}:Keys:{slot}.");
    }
}
