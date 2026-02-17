// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Compression;

/// <summary>
/// An internal stream wrapper that tracks CRC-32 incrementally during reads,
/// handles data descriptor reading after entry data, and supports draining
/// unread bytes on dispose to keep the archive stream properly positioned.
/// </summary>
internal sealed class ZipStreamReaderEntryStream : Stream
{
    private readonly Stream _decompressedStream;
    private readonly Stream _archiveStream;
    private readonly ZipStreamReaderEntry _entry;
    private readonly bool _hasDataDescriptor;
    private readonly bool _useZip64DataDescriptor;
    private uint _computedCrc32;
    private long _totalUncompressedBytesRead;
    private bool _reachedEnd;
    private bool _finalized;
    private bool _isDisposed;

    internal ZipStreamReaderEntryStream(
        Stream decompressedStream,
        Stream archiveStream,
        ZipStreamReaderEntry entry,
        bool hasDataDescriptor,
        bool useZip64DataDescriptor)
    {
        _decompressedStream = decompressedStream;
        _archiveStream = archiveStream;
        _entry = entry;
        _hasDataDescriptor = hasDataDescriptor;
        _useZip64DataDescriptor = useZip64DataDescriptor;
    }

    public override bool CanRead => !_isDisposed;
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

        if (_reachedEnd || buffer.Length == 0)
        {
            return 0;
        }

        int bytesRead = _decompressedStream.Read(buffer);

        if (bytesRead > 0)
        {
            _computedCrc32 = Crc32Helper.UpdateCrc32(_computedCrc32, buffer.Slice(0, bytesRead));
            _totalUncompressedBytesRead += bytesRead;
        }

        if (bytesRead == 0)
        {
            _reachedEnd = true;
        }

        return bytesRead;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_reachedEnd || buffer.Length == 0)
        {
            return 0;
        }

        int bytesRead = await _decompressedStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        if (bytesRead > 0)
        {
            _computedCrc32 = Crc32Helper.UpdateCrc32(_computedCrc32, buffer.Span.Slice(0, bytesRead));
            _totalUncompressedBytesRead += bytesRead;
        }

        if (bytesRead == 0)
        {
            _reachedEnd = true;
        }

        return bytesRead;
    }

    public override int ReadByte()
    {
        byte b = default;
        return Read(new Span<byte>(ref b)) == 1 ? b : -1;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(SR.SeekingNotSupported);
    public override void SetLength(long value) => throw new NotSupportedException(SR.SetLengthRequiresSeekingAndWriting);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(SR.WritingNotSupported);

    /// <summary>
    /// Drains any unread data from the entry stream, reads the data descriptor if needed,
    /// and updates the entry with computed values.
    /// </summary>
    internal void DrainAndFinalize()
    {
        if (_isDisposed)
        {
            return;
        }

        // Drain any remaining decompressed data
        if (!_reachedEnd)
        {
            byte[] drainBuffer = new byte[4096];
            while (Read(drainBuffer) > 0)
            {
            }
        }

        FinalizeEntry();
    }

    /// <summary>
    /// Asynchronously drains any unread data from the entry stream, reads the data descriptor if needed,
    /// and updates the entry with computed values.
    /// </summary>
    internal async ValueTask DrainAndFinalizeAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        // Drain any remaining decompressed data
        if (!_reachedEnd)
        {
            byte[] drainBuffer = new byte[4096];
            while (await ReadAsync(drainBuffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }

        await FinalizeEntryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void FinalizeEntry()
    {
        if (_finalized)
        {
            return;
        }

        _finalized = true;
        _entry.ComputedCrc32 = _computedCrc32;
        _entry.ComputedUncompressedLength = _totalUncompressedBytesRead;
        _entry.IsFullyRead = true;

        if (_hasDataDescriptor)
        {
            ReadDataDescriptor();
        }
    }

    private async ValueTask FinalizeEntryAsync(CancellationToken cancellationToken)
    {
        if (_finalized)
        {
            return;
        }

        _finalized = true;
        _entry.ComputedCrc32 = _computedCrc32;
        _entry.ComputedUncompressedLength = _totalUncompressedBytesRead;
        _entry.IsFullyRead = true;

        if (_hasDataDescriptor)
        {
            await ReadDataDescriptorAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ReadDataDescriptor()
    {
        // Data descriptor may have optional signature (0x08074B50) followed by CRC, compressed size, uncompressed size.
        // Sizes are 32-bit or 64-bit depending on whether Zip64 is used.
        int descriptorSize = _useZip64DataDescriptor
            ? ZipLocalFileHeader.Zip64DataDescriptor.FieldLocations.UncompressedSize + ZipLocalFileHeader.Zip64DataDescriptor.FieldLengths.UncompressedSize
            : ZipLocalFileHeader.ZipDataDescriptor.FieldLocations.UncompressedSize + ZipLocalFileHeader.ZipDataDescriptor.FieldLengths.UncompressedSize;

        Span<byte> descriptorBuffer = stackalloc byte[descriptorSize];
        _archiveStream.ReadExactly(descriptorBuffer);

        ParseDataDescriptor(descriptorBuffer);
    }

    private async ValueTask ReadDataDescriptorAsync(CancellationToken cancellationToken)
    {
        int descriptorSize = _useZip64DataDescriptor
            ? ZipLocalFileHeader.Zip64DataDescriptor.FieldLocations.UncompressedSize + ZipLocalFileHeader.Zip64DataDescriptor.FieldLengths.UncompressedSize
            : ZipLocalFileHeader.ZipDataDescriptor.FieldLocations.UncompressedSize + ZipLocalFileHeader.ZipDataDescriptor.FieldLengths.UncompressedSize;

        byte[] descriptorBuffer = new byte[descriptorSize];
        await _archiveStream.ReadExactlyAsync(descriptorBuffer, cancellationToken).ConfigureAwait(false);

        ParseDataDescriptor(descriptorBuffer);
    }

    private void ParseDataDescriptor(ReadOnlySpan<byte> buffer)
    {
        // Check if the data descriptor has the optional signature
        bool hasSignature = buffer.StartsWith(ZipLocalFileHeader.DataDescriptorSignatureConstantBytes);

        if (!hasSignature)
        {
            // No signature present. The first 4 bytes are CRC32.
            // We need to re-read since we assumed signature was present.
            // For a non-seekable stream this is problematic, so we parse what we have
            // as if there's no signature (CRC32 starts at offset 0).
            ParseDataDescriptorWithoutSignature(buffer);
            return;
        }

        uint crc32 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            buffer[ZipLocalFileHeader.ZipDataDescriptor.FieldLocations.Crc32..]);

        long compressedSize;
        long uncompressedSize;

        if (_useZip64DataDescriptor)
        {
            compressedSize = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(
                buffer[ZipLocalFileHeader.Zip64DataDescriptor.FieldLocations.CompressedSize..]);
            uncompressedSize = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(
                buffer[ZipLocalFileHeader.Zip64DataDescriptor.FieldLocations.UncompressedSize..]);
        }
        else
        {
            compressedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                buffer[ZipLocalFileHeader.ZipDataDescriptor.FieldLocations.CompressedSize..]);
            uncompressedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                buffer[ZipLocalFileHeader.ZipDataDescriptor.FieldLocations.UncompressedSize..]);
        }

        _entry.UpdateFromDataDescriptor(crc32, compressedSize, uncompressedSize);
    }

    private static void ParseDataDescriptorWithoutSignature(ReadOnlySpan<byte> buffer)
    {
        // When there is no signature, the data descriptor starts directly with CRC32
        // and we already read the full descriptor including signature bytes that were actually data.
        // Since we can't seek back on a forward-only stream, we interpret what we have.
        // Without signature: [CRC32 (4)] [CompressedSize (4/8)] [UncompressedSize (4/8)]
        // We already read bytes assuming signature was present, so we have extra bytes read.
        // The simplest approach: the spec recommends always writing the signature.
        // For robustness, throw if the signature is not present on a non-seekable stream.
        throw new InvalidDataException(SR.ZipStreamReaderDataDescriptorSignatureRequired);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            DrainAndFinalize();
            _decompressedStream.Dispose();
            _isDisposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            await DrainAndFinalizeAsync().ConfigureAwait(false);
            await _decompressedStream.DisposeAsync().ConfigureAwait(false);
            _isDisposed = true;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
