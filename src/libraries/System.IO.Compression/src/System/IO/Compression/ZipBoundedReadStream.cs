// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Compression;

/// <summary>
/// A read-only stream that reads exactly a specified number of bytes from the archive stream
/// via the <see cref="ZipStreamReader"/>. Used for entries where the compressed size is known upfront.
/// </summary>
internal sealed class ZipBoundedReadStream : Stream
{
    private readonly ZipStreamReader _reader;
    private long _remaining;
    private bool _isDisposed;

    internal ZipBoundedReadStream(ZipStreamReader reader, long length)
    {
        _reader = reader;
        _remaining = length;
    }

    public override bool CanRead => !_isDisposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_remaining <= 0 || buffer.Length == 0)
        {
            return 0;
        }

        int toRead = (int)Math.Min(buffer.Length, _remaining);
        int bytesRead = _reader.ReadArchive(buffer[..toRead]);
        _remaining -= bytesRead;

        return bytesRead;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_remaining <= 0 || buffer.Length == 0)
        {
            return 0;
        }

        int toRead = (int)Math.Min(buffer.Length, _remaining);
        int bytesRead = await _reader.ReadArchiveAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
        _remaining -= bytesRead;

        return bytesRead;
    }

    public override int ReadByte()
    {
        byte b = default;
        return Read(new Span<byte>(ref b)) == 1 ? b : -1;
    }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    internal long Remaining => _remaining;

    /// <summary>
    /// Drains any remaining bytes from the stream so the archive stream is positioned
    /// past this entry's compressed data.
    /// </summary>
    internal void DrainRemaining()
    {
        if (_remaining > 0)
        {
            _reader.SkipBytes(_remaining);
            _remaining = 0;
        }
    }

    internal async ValueTask DrainRemainingAsync(CancellationToken cancellationToken)
    {
        if (_remaining > 0)
        {
            await _reader.SkipBytesAsync(_remaining, cancellationToken).ConfigureAwait(false);
            _remaining = 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        _isDisposed = true;
        base.Dispose(disposing);
    }
}
