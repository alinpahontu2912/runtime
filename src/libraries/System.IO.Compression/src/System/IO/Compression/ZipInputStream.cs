// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace System.IO.Compression;

/// <summary>
/// Provides a forward-only, read-only <see cref="Stream"/> for reading ZIP archive entries
/// sequentially from local file headers. Reading from this stream returns decompressed entry data.
/// Call <see cref="MoveToNextEntry"/> to advance to the next entry.
/// </summary>
public sealed class ZipInputStream : Stream
{
    private readonly Stream _archiveStream;
    private readonly bool _leaveOpen;
    private readonly Encoding? _entryNameEncoding;
    private ZipInputStreamEntry? _currentEntry;
    private Stream? _decompressedStream;
    private bool _entryFullyRead;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new instance of <see cref="ZipInputStream"/> on the given stream.
    /// </summary>
    /// <param name="stream">The stream containing the ZIP archive to read. Only <see cref="Stream.CanRead"/> is required.</param>
    /// <param name="leaveOpen"><see langword="true"/> to leave the stream open after the <see cref="ZipInputStream"/> is disposed; otherwise, <see langword="false"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> does not support reading.</exception>
    public ZipInputStream(Stream stream, bool leaveOpen = false)
        : this(stream, entryNameEncoding: null, leaveOpen: leaveOpen)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ZipInputStream"/> on the given stream with
    /// the specified encoding.
    /// </summary>
    /// <param name="stream">The stream containing the ZIP archive to read. Only <see cref="Stream.CanRead"/> is required.</param>
    /// <param name="entryNameEncoding">The encoding to use when reading entry names, or <see langword="null"/> to use the default (UTF-8 when the EFS bit is set, otherwise the system default encoding).</param>
    /// <param name="leaveOpen"><see langword="true"/> to leave the stream open after the <see cref="ZipInputStream"/> is disposed; otherwise, <see langword="false"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> does not support reading.</exception>
    public ZipInputStream(Stream stream, Encoding? entryNameEncoding, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
        {
            throw new ArgumentException(SR.ReadModeCapabilities, nameof(stream));
        }

        _archiveStream = stream;
        _leaveOpen = leaveOpen;
        _entryNameEncoding = entryNameEncoding;
    }

    /// <summary>
    /// Gets the metadata for the current entry, or <see langword="null"/> if no entry is current.
    /// </summary>
    public ZipInputStreamEntry? CurrentEntry => _currentEntry;

    /// <inheritdoc/>
    public override bool CanRead => !_isDisposed;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException(SR.SeekingNotSupported);

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException(SR.SeekingNotSupported);
        set => throw new NotSupportedException(SR.SeekingNotSupported);
    }

    /// <summary>
    /// Advances the reader to the next entry in the archive.
    /// If entry data is still unread, it is drained before advancing.
    /// </summary>
    /// <returns><see langword="true"/> if the reader successfully advanced to the next entry; <see langword="false"/> if there are no more entries.</returns>
    /// <exception cref="ObjectDisposedException">The stream has been disposed.</exception>
    /// <exception cref="InvalidDataException">The archive contains corrupt data.</exception>
    public bool MoveToNextEntry()
    {
        ThrowIfDisposed();
        DrainCurrentEntry();

        if (!TryReadLocalFileHeader(out ZipInputStreamEntry? entry))
        {
            _currentEntry = null;
            _decompressedStream = null;
            _entryFullyRead = true;
            return false;
        }

        _currentEntry = entry;
        _decompressedStream = CreateDecompressedStream(entry!);
        _entryFullyRead = false;
        return true;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();

        if (_decompressedStream is null || _entryFullyRead || buffer.Length == 0)
        {
            return 0;
        }

        int bytesRead = _decompressedStream.Read(buffer);

        if (bytesRead == 0)
        {
            _entryFullyRead = true;
        }

        return bytesRead;
    }

    /// <inheritdoc/>
    public override int ReadByte()
    {
        byte b = default;
        return Read(new Span<byte>(ref b)) == 1 ? b : -1;
    }

    /// <inheritdoc/>
    public override void Flush() { }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(SR.SeekingNotSupported);

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException(SR.SetLengthRequiresSeekingAndWriting);

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(SR.WritingNotSupported);

    private bool TryReadLocalFileHeader(out ZipInputStreamEntry? entry)
    {
        entry = null;

        Span<byte> signatureBuffer = stackalloc byte[ZipLocalFileHeader.FieldLengths.Signature];
        int bytesRead = _archiveStream.ReadAtLeast(signatureBuffer, signatureBuffer.Length, throwOnEndOfStream: false);

        if (bytesRead < signatureBuffer.Length)
        {
            return false;
        }

        if (!signatureBuffer.SequenceEqual(ZipLocalFileHeader.SignatureConstantBytes))
        {
            // Any PK-prefixed signature means end of local entries (central directory, EOCD, etc.)
            if (signatureBuffer[0] == 0x50 && signatureBuffer[1] == 0x4B)
            {
                return false;
            }

            throw new InvalidDataException(SR.LocalFileHeaderCorrupt);
        }

        // Read the rest of the fixed-size header (26 bytes after signature)
        int remainingHeaderSize = ZipLocalFileHeader.SizeOfLocalHeader - ZipLocalFileHeader.FieldLengths.Signature;
        Span<byte> headerBuffer = stackalloc byte[remainingHeaderSize];
        _archiveStream.ReadExactly(headerBuffer);

        int offset = 0;
        ushort versionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer[offset..]);
        offset += ZipLocalFileHeader.FieldLengths.VersionNeededToExtract;

        ushort bitFlags = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer[offset..]);
        offset += ZipLocalFileHeader.FieldLengths.GeneralPurposeBitFlags;

        ushort compressionMethodValue = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer[offset..]);
        offset += ZipLocalFileHeader.FieldLengths.CompressionMethod;

        uint lastModified = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer[offset..]);
        offset += ZipLocalFileHeader.FieldLengths.LastModified;

        // Skip CRC32
        offset += ZipLocalFileHeader.FieldLengths.Crc32;

        uint compressedSizeSmall = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer[offset..]);
        offset += ZipLocalFileHeader.FieldLengths.CompressedSize;

        uint uncompressedSizeSmall = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer[offset..]);
        offset += ZipLocalFileHeader.FieldLengths.UncompressedSize;

        ushort filenameLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer[offset..]);
        offset += ZipLocalFileHeader.FieldLengths.FilenameLength;

        ushort extraFieldLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer[offset..]);

        // Read filename
        byte[]? filenameArrayPoolBuffer = null;
        const int StackAllocationThreshold = 512;
        Span<byte> filenameBuffer = filenameLength <= StackAllocationThreshold
            ? stackalloc byte[StackAllocationThreshold].Slice(0, filenameLength)
            : (filenameArrayPoolBuffer = ArrayPool<byte>.Shared.Rent(filenameLength)).AsSpan(0, filenameLength);

        try
        {
            _archiveStream.ReadExactly(filenameBuffer);
        }
        catch
        {
            if (filenameArrayPoolBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(filenameArrayPoolBuffer);
            }
            throw;
        }

        bool isUtf8 = (bitFlags & (ushort)BitFlagValues.UnicodeFileNameAndComment) != 0;
        Encoding encoding = isUtf8 ? Encoding.UTF8 : (_entryNameEncoding ?? Encoding.UTF8);
        string fileName = encoding.GetString(filenameBuffer);

        if (filenameArrayPoolBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(filenameArrayPoolBuffer);
        }

        // Read extra field
        byte[]? extraFieldArrayPoolBuffer = null;
        Span<byte> extraFieldBuffer = extraFieldLength <= StackAllocationThreshold
            ? stackalloc byte[StackAllocationThreshold].Slice(0, extraFieldLength)
            : (extraFieldArrayPoolBuffer = ArrayPool<byte>.Shared.Rent(extraFieldLength)).AsSpan(0, extraFieldLength);

        try
        {
            _archiveStream.ReadExactly(extraFieldBuffer);
        }
        catch
        {
            if (extraFieldArrayPoolBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(extraFieldArrayPoolBuffer);
            }
            throw;
        }

        // Parse Zip64 extra field for large sizes
        long compressedSize = compressedSizeSmall;
        long uncompressedSize = uncompressedSizeSmall;

        bool needsZip64Uncompressed = uncompressedSizeSmall == ZipHelper.Mask32Bit;
        bool needsZip64Compressed = compressedSizeSmall == ZipHelper.Mask32Bit;

        if (needsZip64Uncompressed || needsZip64Compressed)
        {
            Zip64ExtraField zip64 = Zip64ExtraField.GetJustZip64Block(
                extraFieldBuffer,
                readUncompressedSize: needsZip64Uncompressed,
                readCompressedSize: needsZip64Compressed,
                readLocalHeaderOffset: false,
                readStartDiskNumber: false);

            if (needsZip64Uncompressed && zip64.UncompressedSize.HasValue)
            {
                uncompressedSize = zip64.UncompressedSize.Value;
            }

            if (needsZip64Compressed && zip64.CompressedSize.HasValue)
            {
                compressedSize = zip64.CompressedSize.Value;
            }
        }

        if (extraFieldArrayPoolBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(extraFieldArrayPoolBuffer);
        }

        bool hasDataDescriptor = (bitFlags & (ushort)BitFlagValues.DataDescriptor) != 0;
        bool isEncrypted = (bitFlags & (ushort)BitFlagValues.IsEncrypted) != 0;

        if (hasDataDescriptor)
        {
            compressedSize = -1;
            uncompressedSize = -1;
        }

        DateTimeOffset lastModifiedDate = new DateTimeOffset(ZipHelper.DosTimeToDateTime(lastModified));

        entry = new ZipInputStreamEntry(
            name: fileName,
            compressionMethod: (ZipCompressionMethod)compressionMethodValue,
            lastModified: lastModifiedDate,
            compressedLength: compressedSize,
            uncompressedLength: uncompressedSize,
            isEncrypted: isEncrypted,
            hasDataDescriptor: hasDataDescriptor,
            versionNeeded: versionNeeded);

        return true;
    }

    private Stream CreateDecompressedStream(ZipInputStreamEntry entry)
    {
        if (entry.IsEncrypted)
        {
            throw new NotSupportedException(SR.ZipInputStreamEncryptedNotSupported);
        }

        Stream compressedStream;

        if (entry.HasDataDescriptor)
        {
            // When a data descriptor is present, we don't know the compressed size upfront.
            compressedStream = _archiveStream;
        }
        else
        {
            compressedStream = new BoundedReadStream(_archiveStream, entry.CompressedLength);
        }

        return entry.CompressionMethod switch
        {
            ZipCompressionMethod.Stored => compressedStream,
            ZipCompressionMethod.Deflate => new DeflateStream(compressedStream, CompressionMode.Decompress, leaveOpen: true),
            ZipCompressionMethod.Deflate64 => new DeflateManagedStream(compressedStream, ZipCompressionMethod.Deflate64, entry.UncompressedLength),
            _ => throw new NotSupportedException(SR.Format(SR.UnsupportedCompressionMethod, entry.CompressionMethod)),
        };
    }

    private void DrainCurrentEntry()
    {
        if (_decompressedStream is not null && !_entryFullyRead)
        {
            // Drain remaining decompressed data to keep the archive stream positioned correctly
            byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                while (_decompressedStream.Read(buffer) > 0) { }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            _entryFullyRead = true;
        }

        if (_decompressedStream is not null && _decompressedStream != _archiveStream)
        {
            _decompressedStream.Dispose();
            _decompressedStream = null;
        }

        if (_currentEntry is { HasDataDescriptor: true })
        {
            ReadDataDescriptor();
        }
    }

    private void ReadDataDescriptor()
    {
        bool useZip64 = _currentEntry is not null && _currentEntry.VersionNeeded >= (ushort)ZipVersionNeededValues.Zip64;

        int descriptorSize = useZip64
            ? ZipLocalFileHeader.Zip64DataDescriptor.FieldLocations.UncompressedSize + ZipLocalFileHeader.Zip64DataDescriptor.FieldLengths.UncompressedSize
            : ZipLocalFileHeader.ZipDataDescriptor.FieldLocations.UncompressedSize + ZipLocalFileHeader.ZipDataDescriptor.FieldLengths.UncompressedSize;

        Span<byte> descriptorBuffer = stackalloc byte[descriptorSize];
        _archiveStream.ReadExactly(descriptorBuffer);

        // Check for the optional data descriptor signature
        bool hasSignature = descriptorBuffer.StartsWith(ZipLocalFileHeader.DataDescriptorSignatureConstantBytes);

        if (!hasSignature)
        {
            // Without signature, we can't reliably parse on a forward-only stream
            throw new InvalidDataException(SR.ZipInputStreamDataDescriptorSignatureRequired);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            if (_decompressedStream is not null && _decompressedStream != _archiveStream)
            {
                _decompressedStream.Dispose();
            }

            _decompressedStream = null;

            if (!_leaveOpen)
            {
                _archiveStream.Dispose();
            }

            _isDisposed = true;
        }

        base.Dispose(disposing);
    }

    [Flags]
    private enum BitFlagValues : ushort
    {
        IsEncrypted = 0x1,
        DataDescriptor = 0x8,
        UnicodeFileNameAndComment = 0x800,
    }
}
