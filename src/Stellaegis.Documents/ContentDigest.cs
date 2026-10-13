namespace Stellaegis.Documents;

/// <summary>
/// The SHA-256 digest of a content in the form of the HTTP digest fields (RFC 9530):
/// <c>sha-256=:BASE64:</c>, as carried by <c>Repr-Digest</c> and <c>Content-Digest</c>. The module keeps the
/// same digest as lowercase hexadecimal (<see cref="Document.ContentHash"/>).
/// </summary>
public static class ContentDigest
{
    private const string Algorithm = "sha-256";

    /// <summary>Formats a lowercase hexadecimal SHA-256 as a digest field value.</summary>
    public static string Format(string contentHash)
        => $"{Algorithm}=:{Convert.ToBase64String(Convert.FromHexString(contentHash))}:";

    /// <summary>
    /// Reads the SHA-256 entry of a digest field value, which may list several algorithms, as lowercase
    /// hexadecimal.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> with the digest; <see langword="false"/> when the value has no SHA-256 entry, or
    /// a malformed one.
    /// </returns>
    public static bool TryReadSha256(string? value, out string contentHash)
    {
        contentHash = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var member in value.Split(','))
        {
            var entry = member.Trim();
            var equals = entry.IndexOf('=');
            if (equals < 0 || !entry[..equals].Trim().Equals(Algorithm, StringComparison.OrdinalIgnoreCase))
                continue;

            var bytes = entry[(equals + 1)..].Trim();
            if (bytes.Length < 2 || bytes[0] != ':' || bytes[^1] != ':')
                return false;

            try
            {
                var digest = Convert.FromBase64String(bytes[1..^1]);
                if (digest.Length != 32)
                    return false;

                contentHash = Convert.ToHexStringLower(digest);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return false;
    }
}
