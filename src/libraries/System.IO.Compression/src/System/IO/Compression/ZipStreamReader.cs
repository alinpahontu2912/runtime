// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Compression;

/// <summary>
/// Provides a forward-only reader for ZIP archives that reads entries sequentially
/// from local file headers without requiring a seekable stream. This enables streaming
/// decompression from non-seekable sources such as network streams.
/// </summary>
public sealed partial class ZipStreamReader : IDisposable, IAsyncDisposable
{
    private readonly Stream _archiveStream;
    private readonly bool _leaveOpen;
    private readonly Encoding? _entryNameEncoding;
    private readonly bool _tolerant;
    private ZipStreamReaderEntry? _currentEntry;
    private ZipStreamReaderEntryStream? _currentEntryStream;
    private ZipStreamReaderState _state;

    /// <summary>
    /// Initializes a new instance of <see cref="ZipStreamReader"/> on the given stream.
    /// </summary>
    /// <param name="stream">The stream containing the ZIP archive to read. Only <see cref="Stream.CanRead"/> is required.</param>
    /// <param name="leaveOpen"><see langword="true"/> to leave the stream open after the <see cref="ZipStreamReader"/> is disposed; otherwise, <see langword="false"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> does not support reading.</exception>
    public ZipStreamReader(Stream stream, bool leaveOpen = false)
        : this(stream, entryNameEncoding: null, tolerant: false, leaveOpen: leaveOpen)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ZipStreamReader"/> on the given stream with
    /// the specified encoding and tolerance settings.
    /// </summary>
    /// <param name="stream">The stream containing the ZIP archive to read. Only <see cref="Stream.CanRead"/> is required.</param>
    /// <param name="entryNameEncoding">The encoding to use when reading entry names, or <see langword="null"/> to use the default (UTF-8 when the EFS bit is set, otherwise the system default encoding).</param>
    /// <param name="tolerant"><see langword="true"/> to skip entries with corrupt local headers instead of throwing; <see langword="false"/> to throw <see cref="InvalidDataException"/> on corrupt data.</param>
    /// <param name="leaveOpen"><see langword="true"/> to leave the stream open after the <see cref="ZipStreamReader"/> is disposed; otherwise, <see langword="false"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> does not support reading.</exception>
    public ZipStreamReader(Stream stream, Encoding? entryNameEncoding, bool tolerant = false, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
        {
            throw new ArgumentException(SR.ReadModeCapabilities, nameof(stream));
        }

        _archiveStream = stream;
        _leaveOpen = leaveOpen;
        _entryNameEncoding = entryNameEncoding;
        _tolerant = tolerant;
        _state = ZipStreamReaderState.NotStarted;
    }

    /// <summary>
    /// Gets the metadata for the current entry, or <see langword="null"/> if no entry is current.
    /// </summary>
    public ZipStreamReaderEntry? CurrentEntry => _currentEntry;

    /// <summary>
    /// Advances the reader to the next entry in the archive.
    /// If an entry stream is currently open, it is drained and disposed before advancing.
    /// </summary>
    /// <returns><see langword="true"/> if the reader successfully advanced to the next entry; <see langword="false"/> if there are no more entries.</returns>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    /// <exception cref="InvalidDataException">The archive contains corrupt data and tolerant mode is not enabled.</exception>
    public bool MoveToNextEntry()
    {
        ThrowIfDisposed();
        DrainCurrentEntryStream();

        if (!TryReadLocalFileHeader(out ZipStreamReaderEntry? entry))
        {
            _currentEntry = null;
            _state = ZipStreamReaderState.EndOfArchive;
            return false;
        }

        _currentEntry = entry;
        _currentEntryStream = null;
        _state = ZipStreamReaderState.OnEntry;
        return true;
    }

    /// <summary>
    /// Opens the current entry's data for reading as a decompressed stream.
    /// </summary>
    /// <returns>A <see cref="Stream"/> containing the decompressed entry data.</returns>
    /// <exception cref="InvalidOperationException">No current entry exists, or an entry stream is already open.</exception>
    /// <exception cref="NotSupportedException">The entry is encrypted or uses an unsupported compression method.</exception>
    public Stream OpenEntryStream()
    {
        ThrowIfDisposed();

        if (_state != ZipStreamReaderState.OnEntry || _currentEntry is null)
        {
            throw new InvalidOperationException(SR.ZipStreamReaderNoCurrentEntry);
        }

        if (_currentEntryStream is not null)
        {
            throw new InvalidOperationException(SR.ZipStreamReaderEntryStreamAlreadyOpen);
        }

        if (_currentEntry.IsEncrypted)
        {
            throw new NotSupportedException(SR.ZipStreamReaderEncryptedNotSupported);
        }

        ZipStreamReaderEntryStream entryStream = CreateEntryStream(_currentEntry);
        _currentEntryStream = entryStream;
        _state = ZipStreamReaderState.EntryStreamOpen;

        return entryStream;
    }

    private bool TryReadLocalFileHeader(out ZipStreamReaderEntry? entry)
    {
        entry = null;

        // Read the signature (4 bytes)
        Span<byte> signatureBuffer = stackalloc byte[ZipLocalFileHeader.FieldLengths.Signature];
        int bytesRead = _archiveStream.ReadAtLeast(signatureBuffer, signatureBuffer.Length, throwOnEndOfStream: false);

        if (bytesRead < signatureBuffer.Length)
        {
            return false;
        }

        // Check if this is a local file header signature
        if (!signatureBuffer.SequenceEqual(ZipLocalFileHeader.SignatureConstantBytes))
        {
            // Any other PK-prefixed signature means we've reached the end of local file entries
            // (e.g., central directory PK\x01\x02, EOCD PK\x05\x06, Zip64 EOCD PK\x06\x06)
            if (signatureBuffer[0] == 0x50 && signatureBuffer[1] == 0x4B)
            {
                return false;
            }

            if (!_tolerant)
            {
                throw new InvalidDataException(SR.LocalFileHeaderCorrupt);
            }

            return false;
        }

        // Read the rest of the fixed-size header (26 bytes after signature)
        int remainingHeaderSize = ZipLocalFileHeader.SizeOfLocalHeader - ZipLocalFileHeader.FieldLengths.Signature;
        Span<byte> headerBuffer = stackalloc byte[remainingHeaderSize];
        _archiveStream.ReadExactly(headerBuffer);

        // Parse fields from the header buffer (offsets are relative to after signature)
        int offset = 0;
        ushort versionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer[offset..]);
        offset += ZipLocalFileHeader.FieldLengths.VersionNeededToExtract;

        ushort bitFlags = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer[offset..]);
        offset += ZipLocalFileHeader.FieldLengths.GeneralPurposeBitFlags;

        ushort compressionMethodValue = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer[offset..]);
        offset += ZipLocalFileHeader.FieldLengths.CompressionMethod;

        uint lastModified = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer[offset..]);
        offset += ZipLocalFileHeader.FieldLengths.LastModified;

        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer[offset..]);
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

        // Decode filename
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

        // Handle data descriptor flag
        bool hasDataDescriptor = (bitFlags & (ushort)BitFlagValues.DataDescriptor) != 0;
        bool isEncrypted = (bitFlags & (ushort)BitFlagValues.IsEncrypted) != 0;

        if (hasDataDescriptor)
        {
            // When data descriptor is used, the CRC and sizes in the local header may be zero
            compressedSize = -1;
            uncompressedSize = -1;
            crc32 = 0;
        }

        DateTimeOffset lastModifiedDate = new DateTimeOffset(ZipHelper.DosTimeToDateTime(lastModified));

        entry = new ZipStreamReaderEntry(
            name: fileName,
            compressionMethod: (ZipCompressionMethod)compressionMethodValue,
            lastModified: lastModifiedDate,
            crc32: crc32,
            compressedLength: compressedSize,
            uncompressedLength: uncompressedSize,
            generalPurposeBitFlags: bitFlags,
            isEncrypted: isEncrypted,
            hasDataDescriptor: hasDataDescriptor,
            versionNeeded: versionNeeded);

        return true;
    }

    private ZipStreamReaderEntryStream CreateEntryStream(ZipStreamReaderEntry entry)
    {
        bool useZip64DataDescriptor = entry.VersionNeeded >= (ushort)ZipVersionNeededValues.Zip64;

        Stream compressedStream;

        if (entry.HasDataDescriptor)
        {
            // When a data descriptor is present, we don't know the compressed size.
            // We rely on the decompressor to detect end-of-stream.
            // For Stored entries with data descriptors, we can't determine the end without the size,
            // which is a known limitation.
            compressedStream = _archiveStream;
        }
        else
        {
            compressedStream = new BoundedReadStream(_archiveStream, entry.CompressedLength);
        }

        Stream decompressedStream = entry.CompressionMethod switch
        {
            ZipCompressionMethod.Stored => compressedStream,
            ZipCompressionMethod.Deflate => new DeflateStream(compressedStream, CompressionMode.Decompress, leaveOpen: true),
            ZipCompressionMethod.Deflate64 => new DeflateManagedStream(compressedStream, ZipCompressionMethod.Deflate64, entry.UncompressedLength),
            _ => throw new NotSupportedException(SR.Format(SR.UnsupportedCompressionMethod, entry.CompressionMethod)),
        };

        return new ZipStreamReaderEntryStream(
            decompressedStream,
            _archiveStream,
            entry,
            hasDataDescriptor: entry.HasDataDescriptor,
            useZip64DataDescriptor: useZip64DataDescriptor);
    }

    private void DrainCurrentEntryStream()
    {
        if (_currentEntryStream is not null)
        {
            _currentEntryStream.DrainAndFinalize();
            _currentEntryStream.Dispose();
            _currentEntryStream = null;
        }
        else if (_currentEntry is not null && !_currentEntry.IsFullyRead && !_currentEntry.HasDataDescriptor && _currentEntry.CompressedLength > 0)
        {
            // Entry was never opened — skip its compressed data
            SkipBytes(_archiveStream, _currentEntry.CompressedLength);
            _currentEntry.IsFullyRead = true;
        }
    }

    private static void SkipBytes(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (count > 0)
            {
                int toRead = (int)Math.Min(count, buffer.Length);
                int bytesRead = stream.Read(buffer, 0, toRead);
                if (bytesRead == 0)
                {
                    break;
                }

                count -= bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_state == ZipStreamReaderState.Disposed, this);
    }

    /// <summary>
    /// Releases the resources used by the <see cref="ZipStreamReader"/>.
    /// </summary>
    public void Dispose()
    {
        if (_state != ZipStreamReaderState.Disposed)
        {
            _currentEntryStream?.Dispose();
            _currentEntryStream = null;

            if (!_leaveOpen)
            {
                _archiveStream.Dispose();
            }

            _state = ZipStreamReaderState.Disposed;
        }
    }

    /// <summary>
    /// Asynchronously releases the resources used by the <see cref="ZipStreamReader"/>.
    /// </summary>
    /// <returns>A task that represents the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_state != ZipStreamReaderState.Disposed)
        {
            if (_currentEntryStream is not null)
            {
                await _currentEntryStream.DisposeAsync().ConfigureAwait(false);
                _currentEntryStream = null;
            }

            if (!_leaveOpen)
            {
                await _archiveStream.DisposeAsync().ConfigureAwait(false);
            }

            _state = ZipStreamReaderState.Disposed;
        }
    }

    // Internal bit flag values mirroring ZipArchiveEntry's BitFlagValues
    [Flags]
    private enum BitFlagValues : ushort
    {
        IsEncrypted = 0x1,
        DataDescriptor = 0x8,
        UnicodeFileNameAndComment = 0x800,
    }

    private enum ZipStreamReaderState
    {
        NotStarted,
        OnEntry,
        EntryStreamOpen,
        EndOfArchive,
        Disposed,
    }
}
