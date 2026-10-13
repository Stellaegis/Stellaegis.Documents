using System.Security.Cryptography;

namespace Stellaegis.Documents.Client;

/// <summary>
/// Reads a downloaded content while hashing it, and throws at its end when the SHA-256 differs from the one
/// the server announced: damaged content never passes for the original.
/// </summary>
internal sealed class DigestCheckingStream(Stream content, string expectedHash) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _checked;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => content.Length;

    public override long Position
    {
        get => content.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = content.Read(buffer);
        return Hashed(buffer[..read], buffer.Length);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Hashed(buffer.Span[..read], buffer.Length);
    }

    private int Hashed(ReadOnlySpan<byte> read, int requested)
    {
        if (read.Length > 0)
        {
            _hash.AppendData(read);
            return read.Length;
        }

        // Nothing read for a non-empty buffer is the end of the content: the digest is complete. An empty
        // buffer also reads nothing, without meaning the end.
        if (requested > 0 && !_checked)
        {
            _checked = true;
            var actual = Convert.ToHexStringLower(_hash.GetHashAndReset());
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded content does not match its digest: it was damaged on its way.");
        }

        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            content.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _hash.Dispose();
        await content.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
