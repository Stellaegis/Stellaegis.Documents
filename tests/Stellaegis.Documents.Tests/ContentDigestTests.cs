namespace Stellaegis.Documents.Tests;

public sealed class ContentDigestTests
{
    private static readonly string HelloHash = Content.Hash("hello");

    [Fact]
    public void A_digest_round_trips_through_the_field_form()
    {
        var field = ContentDigest.Format(HelloHash);

        Assert.Equal("sha-256=:LPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ=:", field);
        Assert.True(ContentDigest.TryReadSha256(field, out var read));
        Assert.Equal(HelloHash, read);
    }

    [Fact]
    public void The_sha_256_entry_is_found_among_others()
    {
        Assert.True(ContentDigest.TryReadSha256("sha-512=:AAAA:, SHA-256=:LPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ=:", out var read));
        Assert.Equal(HelloHash, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha-512=:AAAA:")]
    [InlineData("sha-256=LPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ=")]
    [InlineData("sha-256=:not base64:")]
    [InlineData("sha-256=:AAAA:")]
    public void A_field_without_a_readable_sha_256_is_not_read(string? field)
    {
        Assert.False(ContentDigest.TryReadSha256(field, out _));
    }
}
