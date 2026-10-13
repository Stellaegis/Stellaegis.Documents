namespace Stellaegis.Documents.Client;

/// <summary>
/// The content stream of an HTTP response, which owns that response: disposing the stream disposes the
/// response with it, so the caller of a download has one thing to dispose.
/// </summary>
internal sealed class ResponseContentStream(HttpResponseMessage response, Stream content) : Stream
{
    public override bool CanRead => content.CanRead;
    public override bool CanSeek => content.CanSeek;
    public override bool CanWrite => false;
    public override long Length => content.Length;

    public override long Position
    {
        get => content.Position;
        set => content.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => content.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => content.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => content.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => content.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => content.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            content.Dispose();
            response.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await content.DisposeAsync().ConfigureAwait(false);
        response.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
