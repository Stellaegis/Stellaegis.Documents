using System.Security.Cryptography;

namespace Stellaegis.Documents;

/// <summary>
/// Passes a content through while hashing (SHA-256) and counting it, so the service can store a content
/// of any size with a few buffers in memory and learn its digest once it is written.
/// </summary>
internal sealed class HashingStream(Stream content) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _contentHash;

    /// <summary>The bytes read so far.</summary>
    public long ContentLength { get; private set; }

    /// <summary>
    /// The SHA-256 (hex, lowercase) of the whole content.
    /// </summary>
    /// <exception cref="InvalidOperationException">The content was not read to its end.</exception>
    public string ContentHash => _contentHash
        ?? throw new InvalidOperationException("The content was not read to its end: its digest is unknown.");

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => ContentLength;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => Hashed(buffer[..content.Read(buffer)], buffer.Length);

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
            ContentLength += read.Length;
        }
        else if (requested > 0)
        {
            // Nothing read for a non-empty buffer: the end of the content.
            _contentHash ??= Convert.ToHexStringLower(_hash.GetHashAndReset());
        }

        return read.Length;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // The caller owns the content it handed over: only the hash is released here.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hash.Dispose();

        base.Dispose(disposing);
    }
}
