// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Compression;

/// <summary>
/// A read-only stream wrapper that limits the number of bytes that can be read from an underlying stream.
/// Unlike <see cref="SubReadStream"/>, this does not require the underlying stream to be seekable.
/// </summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _baseStream;
    private long _remaining;
    private bool _isDisposed;

    internal BoundedReadStream(Stream baseStream, long length)
    {
        _baseStream = baseStream;
        _remaining = length;
    }

    public override bool CanRead => !_isDisposed && _baseStream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(SR.SeekingNotSupported);
    public override long Position
    {
        get => throw new NotSupportedException(SR.SeekingNotSupported);
        set => throw new NotSupportedException(SR.SeekingNotSupported);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();

        if (_remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > _remaining)
        {
            buffer = buffer.Slice(0, (int)_remaining);
        }

        int bytesRead = _baseStream.Read(buffer);
        _remaining -= bytesRead;

        return bytesRead;
    }

    public override int ReadByte()
    {
        byte b = default;
        return Read(new Span<byte>(ref b)) == 1 ? b : -1;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > _remaining)
        {
            buffer = buffer.Slice(0, (int)_remaining);
        }

        int bytesRead = await _baseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _remaining -= bytesRead;

        return bytesRead;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(SR.SeekingNotSupported);
    public override void SetLength(long value) => throw new NotSupportedException(SR.SetLengthRequiresSeekingAndWriting);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(SR.WritingNotSupported);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            // Do not close the base stream — it is owned by the ZipInputStream
            _isDisposed = true;
        }
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
