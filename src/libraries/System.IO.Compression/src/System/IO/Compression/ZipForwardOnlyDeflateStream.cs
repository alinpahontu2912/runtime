// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Compression;

/// <summary>
/// A read-only stream that decompresses Deflate data from a ZIP archive for entries
/// that use data descriptors (bit 3 set, compressed size unknown upfront).
/// Uses the <see cref="Inflater"/> directly to track unconsumed input bytes after
/// decompression finishes, enabling correct positioning of the archive stream.
/// </summary>
internal sealed class ZipForwardOnlyDeflateStream : Stream
{
    private const int ReadBufferSize = 4096;

    private readonly ZipStreamReader _reader;
    private readonly Inflater _inflater;
    private byte[] _readBuffer;
    private int _lastReadCount;
    private bool _finished;
    private bool _isDisposed;

    internal ZipForwardOnlyDeflateStream(ZipStreamReader reader)
    {
        _reader = reader;
        _inflater = Inflater.CreateInflater(-15); // Raw deflate (negative windowBits)
        _readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
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

        if (_finished || buffer.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            int bytesDecompressed = _inflater.Inflate(buffer);
            if (bytesDecompressed > 0)
            {
                return bytesDecompressed;
            }

            if (_inflater.Finished())
            {
                HandleDecompressionFinished();
                return 0;
            }

            if (_inflater.NeedsInput())
            {
                int n = _reader.ReadArchive(_readBuffer.AsSpan(0, ReadBufferSize));
                if (n == 0)
                {
                    _finished = true;
                    return 0;
                }
                _lastReadCount = n;
                _inflater.SetInput(_readBuffer, 0, n);
            }
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_finished || buffer.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            int bytesDecompressed = _inflater.Inflate(buffer.Span);
            if (bytesDecompressed > 0)
            {
                return bytesDecompressed;
            }

            if (_inflater.Finished())
            {
                HandleDecompressionFinished();
                return 0;
            }

            if (_inflater.NeedsInput())
            {
                int n = await _reader.ReadArchiveAsync(_readBuffer.AsMemory(0, ReadBufferSize), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    _finished = true;
                    return 0;
                }
                _lastReadCount = n;
                _inflater.SetInput(_readBuffer, 0, n);
            }
        }
    }

    public override int ReadByte()
    {
        byte b = default;
        return Read(new Span<byte>(ref b)) == 1 ? b : -1;
    }

    /// <summary>
    /// After inflation finishes, pushes unconsumed input bytes back to the reader
    /// so the data descriptor can be read correctly.
    /// </summary>
    private void HandleDecompressionFinished()
    {
        _finished = true;

        int availableInput = _inflater.GetAvailableInput();
        if (availableInput > 0 && _lastReadCount > 0)
        {
            // The unconsumed bytes are at the end of the last read buffer
            int unconsumedStart = _lastReadCount - availableInput;
            _reader.PushBack(_readBuffer, unconsumedStart, availableInput);
        }
    }

    /// <summary>
    /// Drains the deflate stream to the end if not already finished,
    /// then handles the data descriptor.
    /// </summary>
    internal void DrainToEnd()
    {
        if (!_finished)
        {
            Span<byte> drainBuffer = stackalloc byte[4096];
            while (Read(drainBuffer) > 0) { }
        }
    }

    internal async ValueTask DrainToEndAsync(CancellationToken cancellationToken)
    {
        if (!_finished)
        {
            byte[] drainBuffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                while (await ReadAsync(drainBuffer, cancellationToken).ConfigureAwait(false) > 0) { }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(drainBuffer);
            }
        }
    }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            if (disposing)
            {
                _inflater.Dispose();
                if (_readBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(_readBuffer);
                    _readBuffer = null!;
                }
            }
        }
        base.Dispose(disposing);
    }
}
