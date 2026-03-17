// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Compression;

/// <summary>
/// Provides a forward-only reader for reading ZIP archive entries from a stream
/// that does not need to be seekable.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="ZipArchive"/>, which requires a seekable stream and reads the
/// central directory at the end of the file, <see cref="ZipStreamReader"/> walks
/// local file headers sequentially and decompresses entry data on the fly. This makes
/// it suitable for network streams, pipes, and other non-seekable sources.
/// </para>
/// <para>
/// Call <see cref="GetNextEntry(bool)"/> or <see cref="GetNextEntryAsync(bool, CancellationToken)"/>
/// to advance to the next entry. Each returned <see cref="ZipStreamReaderEntry"/> exposes
/// metadata and a <see cref="ZipStreamReaderEntry.DataStream"/> with the decompressed data.
/// </para>
/// </remarks>
public sealed class ZipStreamReader : IDisposable, IAsyncDisposable
{
    private readonly Stream _archiveStream;
    private readonly Encoding? _entryNameEncoding;
    private readonly bool _leaveOpen;
    private bool _isDisposed;
    private bool _reachedEnd;
    private ZipStreamReaderEntry? _previousEntry;
    private List<Stream>? _dataStreamsToDispose;
    private byte[]? _pushbackBuffer;
    private int _pushbackOffset;
    private int _pushbackCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZipStreamReader"/> class for reading
    /// ZIP entries from the specified stream.
    /// </summary>
    /// <param name="stream">The stream containing the ZIP archive to read.</param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave the <paramref name="stream"/> open after the
    /// <see cref="ZipStreamReader"/> object is disposed; otherwise, <see langword="false"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> does not support reading.</exception>
    public ZipStreamReader(Stream stream, bool leaveOpen = false)
        : this(stream, entryNameEncoding: null, leaveOpen)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ZipStreamReader"/> class for reading
    /// ZIP entries from the specified stream, using the specified encoding for entry names.
    /// </summary>
    /// <param name="stream">The stream containing the ZIP archive to read.</param>
    /// <param name="entryNameEncoding">
    /// The encoding to use when reading entry names. If <see langword="null"/>, the encoding
    /// is determined from the entry's general purpose bit flags (UTF-8 if bit 11 is set,
    /// otherwise the default ZIP encoding).
    /// </param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to leave the <paramref name="stream"/> open after the
    /// <see cref="ZipStreamReader"/> object is disposed; otherwise, <see langword="false"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> does not support reading.</exception>
    public ZipStreamReader(Stream stream, Encoding? entryNameEncoding, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
        {
            throw new ArgumentException(SR.NotSupported_UnreadableStream, nameof(stream));
        }

        _archiveStream = stream;
        _entryNameEncoding = entryNameEncoding;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Retrieves the next entry from the archive stream.
    /// </summary>
    /// <param name="copyData">
    /// <para>Set to <see langword="true"/> to copy the decompressed data of the entry into a new
    /// <see cref="MemoryStream"/>. The entry's <see cref="ZipStreamReaderEntry.DataStream"/>
    /// remains valid after advancing to the next entry.</para>
    /// <para>Set to <see langword="false"/> if the data should not be copied. The caller must
    /// read the <see cref="ZipStreamReaderEntry.DataStream"/> before calling
    /// <see cref="GetNextEntry(bool)"/> again, because the stream is invalidated when the
    /// reader advances.</para>
    /// <para>The default value is <see langword="false"/>.</para>
    /// </param>
    /// <returns>
    /// A <see cref="ZipStreamReaderEntry"/> if a valid entry was found, or <see langword="null"/>
    /// if the end of the archive has been reached.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    /// <exception cref="InvalidDataException">The archive data is malformed.</exception>
    /// <exception cref="NotSupportedException">
    /// The entry uses a Stored compression method with a data descriptor, which is not supported.
    /// </exception>
    public ZipStreamReaderEntry? GetNextEntry(bool copyData = false)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_reachedEnd)
        {
            return null;
        }

        AdvancePastPreviousEntry();

        return ReadNextEntry(copyData);
    }

    /// <summary>
    /// Asynchronously retrieves the next entry from the archive stream.
    /// </summary>
    /// <param name="copyData">
    /// <para>Set to <see langword="true"/> to copy the decompressed data of the entry into a new
    /// <see cref="MemoryStream"/>. The entry's <see cref="ZipStreamReaderEntry.DataStream"/>
    /// remains valid after advancing to the next entry.</para>
    /// <para>Set to <see langword="false"/> if the data should not be copied. The caller must
    /// read the <see cref="ZipStreamReaderEntry.DataStream"/> before calling
    /// <see cref="GetNextEntryAsync(bool, CancellationToken)"/> again, because the stream is
    /// invalidated when the reader advances.</para>
    /// <para>The default value is <see langword="false"/>.</para>
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A value task containing a <see cref="ZipStreamReaderEntry"/> if a valid entry was found,
    /// or <see langword="null"/> if the end of the archive has been reached.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    /// <exception cref="InvalidDataException">The archive data is malformed.</exception>
    /// <exception cref="NotSupportedException">
    /// The entry uses a Stored compression method with a data descriptor, which is not supported.
    /// </exception>
    public ValueTask<ZipStreamReaderEntry?> GetNextEntryAsync(bool copyData = false, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<ZipStreamReaderEntry?>(cancellationToken);
        }

        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_reachedEnd)
        {
            return ValueTask.FromResult<ZipStreamReaderEntry?>(null);
        }

        return ReadNextEntryInternalAsync(copyData, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _previousEntry?.InvalidateDataStream();

            if (_dataStreamsToDispose is not null)
            {
                foreach (Stream stream in _dataStreamsToDispose)
                {
                    stream.Dispose();
                }

                _dataStreamsToDispose = null;
            }

            if (!_leaveOpen)
            {
                _archiveStream.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _previousEntry?.InvalidateDataStream();

            if (_dataStreamsToDispose is not null)
            {
                foreach (Stream stream in _dataStreamsToDispose)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                _dataStreamsToDispose = null;
            }

            if (!_leaveOpen)
            {
                await _archiveStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void AdvancePastPreviousEntry()
    {
        if (_previousEntry is null)
        {
            return;
        }

        _previousEntry.AdvancePastData(this);
        _previousEntry = null;
    }

    private async ValueTask AdvancePastPreviousEntryAsync(CancellationToken cancellationToken)
    {
        if (_previousEntry is null)
        {
            return;
        }

        await _previousEntry.AdvancePastDataAsync(this, cancellationToken).ConfigureAwait(false);
        _previousEntry = null;
    }

    private ZipStreamReaderEntry? ReadNextEntry(bool copyData)
    {
        Span<byte> signatureBuffer = stackalloc byte[4];
        ReadExactly(signatureBuffer);

        if (!signatureBuffer.SequenceEqual(ZipLocalFileHeader.SignatureConstantBytes))
        {
            if (signatureBuffer[0] == 0x50 && signatureBuffer[1] == 0x4B)
            {
                // PK-prefixed signature but not a local file header — end of local entries.
                _reachedEnd = true;
                return null;
            }

            throw new InvalidDataException(SR.LocalFileHeaderCorrupt);
        }

        Span<byte> headerBuffer = stackalloc byte[ZipLocalFileHeader.SizeOfLocalHeader - ZipLocalFileHeader.FieldLengths.Signature];
        ReadExactly(headerBuffer);

        return ParseEntryFromHeader(headerBuffer, copyData);
    }

    private async ValueTask<ZipStreamReaderEntry?> ReadNextEntryInternalAsync(bool copyData, CancellationToken cancellationToken)
    {
        await AdvancePastPreviousEntryAsync(cancellationToken).ConfigureAwait(false);

        byte[] signatureBuffer = new byte[4];
        await ReadExactlyAsync(signatureBuffer, cancellationToken).ConfigureAwait(false);

        if (!signatureBuffer.AsSpan().SequenceEqual(ZipLocalFileHeader.SignatureConstantBytes))
        {
            if (signatureBuffer[0] == 0x50 && signatureBuffer[1] == 0x4B)
            {
                _reachedEnd = true;
                return null;
            }

            throw new InvalidDataException(SR.LocalFileHeaderCorrupt);
        }

        byte[] headerBuffer = new byte[ZipLocalFileHeader.SizeOfLocalHeader - ZipLocalFileHeader.FieldLengths.Signature];
        await ReadExactlyAsync(headerBuffer, cancellationToken).ConfigureAwait(false);

        return await ParseEntryFromHeaderAsync(headerBuffer, copyData, cancellationToken).ConfigureAwait(false);
    }

    private ZipStreamReaderEntry ParseEntryFromHeader(ReadOnlySpan<byte> header, bool copyData)
    {
        // The header buffer starts after the 4-byte signature, so subtract 4 from all field locations.
        const int Offset = ZipLocalFileHeader.FieldLengths.Signature;
        ushort versionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(header[(ZipLocalFileHeader.FieldLocations.VersionNeededToExtract - Offset)..]);
        ushort generalPurposeBitFlags = BinaryPrimitives.ReadUInt16LittleEndian(header[(ZipLocalFileHeader.FieldLocations.GeneralPurposeBitFlags - Offset)..]);
        ushort compressionMethodValue = BinaryPrimitives.ReadUInt16LittleEndian(header[(ZipLocalFileHeader.FieldLocations.CompressionMethod - Offset)..]);
        uint lastModified = BinaryPrimitives.ReadUInt32LittleEndian(header[(ZipLocalFileHeader.FieldLocations.LastModified - Offset)..]);
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(header[(ZipLocalFileHeader.FieldLocations.Crc32 - Offset)..]);
        uint compressedSizeSmall = BinaryPrimitives.ReadUInt32LittleEndian(header[(ZipLocalFileHeader.FieldLocations.CompressedSize - Offset)..]);
        uint uncompressedSizeSmall = BinaryPrimitives.ReadUInt32LittleEndian(header[(ZipLocalFileHeader.FieldLocations.UncompressedSize - Offset)..]);
        ushort filenameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[(ZipLocalFileHeader.FieldLocations.FilenameLength - Offset)..]);
        ushort extraFieldLength = BinaryPrimitives.ReadUInt16LittleEndian(header[(ZipLocalFileHeader.FieldLocations.ExtraFieldLength - Offset)..]);

        bool hasDataDescriptor = (generalPurposeBitFlags & (1 << 3)) != 0;

        // Read filename
        byte[]? filenameArrayPool = null;
        Span<byte> filenameBuffer = filenameLength <= 256
            ? stackalloc byte[filenameLength]
            : (filenameArrayPool = ArrayPool<byte>.Shared.Rent(filenameLength)).AsSpan(0, filenameLength);
        try
        {
            ReadExactly(filenameBuffer);
        }
        catch
        {
            if (filenameArrayPool is not null)
            {
                ArrayPool<byte>.Shared.Return(filenameArrayPool);
            }
            throw;
        }

        string fullName = DecodeEntryName(filenameBuffer);

        if (filenameArrayPool is not null)
        {
            ArrayPool<byte>.Shared.Return(filenameArrayPool);
        }

        // Read extra field to check for Zip64 sizes
        long compressedSize = compressedSizeSmall;
        long uncompressedSize = uncompressedSizeSmall;

        if (extraFieldLength > 0)
        {
            byte[]? extraFieldArrayPool = null;
            Span<byte> extraFieldBuffer = extraFieldLength <= 512
                ? stackalloc byte[extraFieldLength]
                : (extraFieldArrayPool = ArrayPool<byte>.Shared.Rent(extraFieldLength)).AsSpan(0, extraFieldLength);
            try
            {
                ReadExactly(extraFieldBuffer);

                // Check for Zip64 extra field
                bool needUncompressedSize = uncompressedSizeSmall == ZipHelper.Mask32Bit;
                bool needCompressedSize = compressedSizeSmall == ZipHelper.Mask32Bit;
                if (needUncompressedSize || needCompressedSize)
                {
                    Zip64ExtraField zip64 = Zip64ExtraField.GetJustZip64Block(
                        extraFieldBuffer,
                        readUncompressedSize: needUncompressedSize,
                        readCompressedSize: needCompressedSize,
                        readLocalHeaderOffset: false,
                        readStartDiskNumber: false);

                    if (needUncompressedSize && zip64.UncompressedSize.HasValue)
                    {
                        uncompressedSize = zip64.UncompressedSize.Value;
                    }

                    if (needCompressedSize && zip64.CompressedSize.HasValue)
                    {
                        compressedSize = zip64.CompressedSize.Value;
                    }
                }
            }
            finally
            {
                if (extraFieldArrayPool is not null)
                {
                    ArrayPool<byte>.Shared.Return(extraFieldArrayPool);
                }
            }
        }

        ZipCompressionMethod compressionMethod = compressionMethodValue switch
        {
            0 => ZipCompressionMethod.Stored,
            8 => ZipCompressionMethod.Deflate,
            9 => ZipCompressionMethod.Deflate64,
            _ => (ZipCompressionMethod)compressionMethodValue,
        };

        DateTimeOffset lastModifiedDto = new DateTimeOffset(ZipHelper.DosTimeToDateTime(lastModified));

        bool isDirectory = fullName.Length > 0 && (fullName[^1] is '/' or '\\');

        // When data descriptor is used, sizes/crc may be zero in local header
        if (hasDataDescriptor)
        {
            crc32 = 0;
            compressedSize = 0;
            uncompressedSize = 0;
        }

        Stream? dataStream = null;
        ZipBoundedReadStream? boundedStream = null;
        if (!isDirectory)
        {
            dataStream = CreateDataStream(compressionMethod, compressedSize, hasDataDescriptor, copyData, out boundedStream);
        }

        var entry = new ZipStreamReaderEntry(
            fullName: fullName,
            compressionMethod: compressionMethod,
            lastModified: lastModifiedDto,
            crc32: crc32,
            compressedLength: compressedSize,
            length: uncompressedSize,
            generalPurposeBitFlags: generalPurposeBitFlags,
            versionNeeded: versionNeeded,
            dataStream: dataStream,
            hasDataDescriptor: hasDataDescriptor,
            boundedStream: boundedStream,
            dataCopied: copyData);

        _previousEntry = entry;

        if (copyData && dataStream is not null)
        {
            (_dataStreamsToDispose ??= new List<Stream>()).Add(dataStream);
        }

        return entry;
    }

    private async ValueTask<ZipStreamReaderEntry> ParseEntryFromHeaderAsync(byte[] header, bool copyData, CancellationToken cancellationToken)
    {
        const int Offset = ZipLocalFileHeader.FieldLengths.Signature;
        ushort versionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(ZipLocalFileHeader.FieldLocations.VersionNeededToExtract - Offset));
        ushort generalPurposeBitFlags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(ZipLocalFileHeader.FieldLocations.GeneralPurposeBitFlags - Offset));
        ushort compressionMethodValue = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(ZipLocalFileHeader.FieldLocations.CompressionMethod - Offset));
        uint lastModified = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(ZipLocalFileHeader.FieldLocations.LastModified - Offset));
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(ZipLocalFileHeader.FieldLocations.Crc32 - Offset));
        uint compressedSizeSmall = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(ZipLocalFileHeader.FieldLocations.CompressedSize - Offset));
        uint uncompressedSizeSmall = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(ZipLocalFileHeader.FieldLocations.UncompressedSize - Offset));
        ushort filenameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(ZipLocalFileHeader.FieldLocations.FilenameLength - Offset));
        ushort extraFieldLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(ZipLocalFileHeader.FieldLocations.ExtraFieldLength - Offset));

        bool hasDataDescriptor = (generalPurposeBitFlags & (1 << 3)) != 0;

        byte[] filenameBytes = new byte[filenameLength];
        await ReadExactlyAsync(filenameBytes, cancellationToken).ConfigureAwait(false);
        string fullName = DecodeEntryName(filenameBytes);

        long compressedSize = compressedSizeSmall;
        long uncompressedSize = uncompressedSizeSmall;

        if (extraFieldLength > 0)
        {
            byte[] extraFieldBytes = new byte[extraFieldLength];
            await ReadExactlyAsync(extraFieldBytes, cancellationToken).ConfigureAwait(false);

            bool needUncompressedSize = uncompressedSizeSmall == ZipHelper.Mask32Bit;
            bool needCompressedSize = compressedSizeSmall == ZipHelper.Mask32Bit;
            if (needUncompressedSize || needCompressedSize)
            {
                Zip64ExtraField zip64 = Zip64ExtraField.GetJustZip64Block(
                    extraFieldBytes,
                    readUncompressedSize: needUncompressedSize,
                    readCompressedSize: needCompressedSize,
                    readLocalHeaderOffset: false,
                    readStartDiskNumber: false);

                if (needUncompressedSize && zip64.UncompressedSize.HasValue)
                {
                    uncompressedSize = zip64.UncompressedSize.Value;
                }

                if (needCompressedSize && zip64.CompressedSize.HasValue)
                {
                    compressedSize = zip64.CompressedSize.Value;
                }
            }
        }

        ZipCompressionMethod compressionMethod = compressionMethodValue switch
        {
            0 => ZipCompressionMethod.Stored,
            8 => ZipCompressionMethod.Deflate,
            9 => ZipCompressionMethod.Deflate64,
            _ => (ZipCompressionMethod)compressionMethodValue,
        };

        DateTimeOffset lastModifiedDto = new DateTimeOffset(ZipHelper.DosTimeToDateTime(lastModified));
        bool isDirectory = fullName.Length > 0 && (fullName[^1] is '/' or '\\');

        if (hasDataDescriptor)
        {
            crc32 = 0;
            compressedSize = 0;
            uncompressedSize = 0;
        }

        Stream? dataStream = null;
        ZipBoundedReadStream? boundedStream = null;
        if (!isDirectory)
        {
            dataStream = CreateDataStream(compressionMethod, compressedSize, hasDataDescriptor, copyData, out boundedStream);

            if (copyData && dataStream is not MemoryStream && dataStream is not null)
            {
                MemoryStream memoryStream = new MemoryStream();
                await dataStream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
                await dataStream.DisposeAsync().ConfigureAwait(false);
                memoryStream.Position = 0;
                dataStream = memoryStream;
            }
        }

        var entry = new ZipStreamReaderEntry(
            fullName: fullName,
            compressionMethod: compressionMethod,
            lastModified: lastModifiedDto,
            crc32: crc32,
            compressedLength: compressedSize,
            length: uncompressedSize,
            generalPurposeBitFlags: generalPurposeBitFlags,
            versionNeeded: versionNeeded,
            dataStream: dataStream,
            hasDataDescriptor: hasDataDescriptor,
            boundedStream: boundedStream,
            dataCopied: copyData);

        _previousEntry = entry;

        if (copyData && dataStream is not null)
        {
            (_dataStreamsToDispose ??= new List<Stream>()).Add(dataStream);
        }

        return entry;
    }

    private Stream CreateDataStream(ZipCompressionMethod compressionMethod, long compressedSize, bool hasDataDescriptor, bool copyData, out ZipBoundedReadStream? outBoundedStream)
    {
        outBoundedStream = null;

        if (hasDataDescriptor && compressedSize == 0)
        {
            // Data descriptor case: compressed size is unknown
            if (compressionMethod is ZipCompressionMethod.Stored)
            {
                throw new NotSupportedException(SR.ZipStreamReaderStoredDataDescriptorNotSupported);
            }

            if (compressionMethod is ZipCompressionMethod.Deflate)
            {
                // For Deflate + data descriptor, use a custom stream that uses the Inflater
                // directly so we can track unconsumed input bytes after decompression finishes.
                var deflateStream = new ZipForwardOnlyDeflateStream(this);
                if (copyData)
                {
                    return CopyToMemoryStream(deflateStream);
                }
                return deflateStream;
            }

            throw new NotSupportedException(SR.Format(SR.UnsupportedCompressionMethod, compressionMethod));
        }

        // Known compressed size case
        var boundedStream = new ZipBoundedReadStream(this, compressedSize);

        Stream decompressedStream = compressionMethod switch
        {
            ZipCompressionMethod.Stored => boundedStream,
            ZipCompressionMethod.Deflate => new DeflateStream(boundedStream, CompressionMode.Decompress, leaveOpen: true),
            ZipCompressionMethod.Deflate64 => new DeflateManagedStream(boundedStream, ZipCompressionMethod.Deflate64),
            _ => throw new NotSupportedException(SR.Format(SR.UnsupportedCompressionMethod, compressionMethod)),
        };

        if (copyData)
        {
            MemoryStream ms = CopyToMemoryStream(decompressedStream);
            outBoundedStream = boundedStream;
            return ms;
        }

        outBoundedStream = compressionMethod is ZipCompressionMethod.Stored ? null : boundedStream;
        return decompressedStream;
    }

    private static MemoryStream CopyToMemoryStream(Stream source)
    {
        MemoryStream memoryStream = new MemoryStream();
        source.CopyTo(memoryStream);
        source.Dispose();
        memoryStream.Position = 0;
        return memoryStream;
    }

    private string DecodeEntryName(ReadOnlySpan<byte> filenameBytes)
    {

        if (_entryNameEncoding is not null)
        {
            return _entryNameEncoding.GetString(filenameBytes);
        }

        return Encoding.UTF8.GetString(filenameBytes);
    }

    // Reads from the archive stream, serving pushback bytes first.
    internal int ReadArchive(Span<byte> buffer)
    {
        if (_pushbackCount > 0)
        {
            int toCopy = Math.Min(buffer.Length, _pushbackCount);
            _pushbackBuffer.AsSpan(_pushbackOffset, toCopy).CopyTo(buffer);
            _pushbackOffset += toCopy;
            _pushbackCount -= toCopy;
            return toCopy;
        }

        return _archiveStream.Read(buffer);
    }

    internal async ValueTask<int> ReadArchiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_pushbackCount > 0)
        {
            int toCopy = Math.Min(buffer.Length, _pushbackCount);
            _pushbackBuffer.AsMemory(_pushbackOffset, toCopy).CopyTo(buffer);
            _pushbackOffset += toCopy;
            _pushbackCount -= toCopy;
            return toCopy;
        }

        return await _archiveStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    // Pushes bytes back so they'll be returned by the next ReadArchive call.
    internal void PushBack(byte[] buffer, int offset, int count)
    {
        if (count == 0)
        {
            return;
        }

        Debug.Assert(_pushbackCount == 0, "Pushback buffer still has unconsumed data.");

        if (_pushbackBuffer is null || _pushbackBuffer.Length < count)
        {
            _pushbackBuffer = new byte[count];
        }

        Buffer.BlockCopy(buffer, offset, _pushbackBuffer, 0, count);
        _pushbackOffset = 0;
        _pushbackCount = count;
    }

    private void ReadExactly(Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = ReadArchive(buffer[totalRead..]);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException();
            }
            totalRead += bytesRead;
        }
    }

    private async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = await ReadArchiveAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException();
            }
            totalRead += bytesRead;
        }
    }

    internal void SkipBytes(long count)
    {
        if (count <= 0)
        {
            return;
        }

        byte[]? poolBuffer = null;
        try
        {
            Span<byte> buffer = count <= 4096
                ? stackalloc byte[(int)count]
                : (poolBuffer = ArrayPool<byte>.Shared.Rent(4096));

            long remaining = count;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int bytesRead = ReadArchive(buffer[..toRead]);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException();
                }
                remaining -= bytesRead;
            }
        }
        finally
        {
            if (poolBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(poolBuffer);
            }
        }
    }

    internal async ValueTask SkipBytesAsync(long count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            long remaining = count;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int bytesRead = await ReadArchiveAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException();
                }
                remaining -= bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal void ReadDataDescriptor(ZipStreamReaderEntry entry)
    {
        Span<byte> buffer = stackalloc byte[24]; // Max data descriptor size (Zip64 with signature)
        int bytesRead = 0;

        // Read at least 4 bytes to check for optional signature
        ReadExactly(buffer[..4]);
        bytesRead = 4;

        bool hasSignature = buffer[..4].SequenceEqual(ZipLocalFileHeader.DataDescriptorSignatureConstantBytes);

        if (hasSignature)
        {
            // Signature present: read CRC-32 + sizes
            ReadExactly(buffer[4..16]);
            bytesRead = 16;
        }

        int crc32Offset = hasSignature ? 4 : 0;
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(buffer[crc32Offset..]);

        bool isZip64 = entry.VersionNeeded >= 45;

        if (isZip64)
        {
            int sizesStart = crc32Offset + 4;
            int totalNeeded = sizesStart + 16;
            if (bytesRead < totalNeeded)
            {
                ReadExactly(buffer[bytesRead..totalNeeded]);
            }
            long compressedSize = BinaryPrimitives.ReadInt64LittleEndian(buffer[(sizesStart)..]);
            long uncompressedSize = BinaryPrimitives.ReadInt64LittleEndian(buffer[(sizesStart + 8)..]);
            entry.UpdateFromDataDescriptor(crc32, compressedSize, uncompressedSize);
        }
        else
        {
            int sizesStart = crc32Offset + 4;
            int totalNeeded = sizesStart + 8;
            if (bytesRead < totalNeeded)
            {
                ReadExactly(buffer[bytesRead..totalNeeded]);
            }
            uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(sizesStart)..]);
            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(sizesStart + 4)..]);
            entry.UpdateFromDataDescriptor(crc32, compressedSize, uncompressedSize);
        }
    }

    internal async ValueTask ReadDataDescriptorAsync(ZipStreamReaderEntry entry, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[24]; // Max data descriptor size (Zip64 with signature)
        // Read at least 4 bytes to check for optional signature
        await ReadExactlyAsync(buffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        int bytesRead = 4;

        bool hasSignature = buffer.AsSpan(0, 4).SequenceEqual(ZipLocalFileHeader.DataDescriptorSignatureConstantBytes);

        if (hasSignature)
        {
            // Signature present: read CRC-32 + sizes
            await ReadExactlyAsync(buffer.AsMemory(4, 12), cancellationToken).ConfigureAwait(false);
            bytesRead = 16;
        }

        int crc32Offset = hasSignature ? 4 : 0;
        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(crc32Offset));

        bool isZip64 = entry.VersionNeeded >= 45;

        if (isZip64)
        {
            int sizesStart = crc32Offset + 4;
            int totalNeeded = sizesStart + 16;
            if (bytesRead < totalNeeded)
            {
                await ReadExactlyAsync(buffer.AsMemory(bytesRead, totalNeeded - bytesRead), cancellationToken).ConfigureAwait(false);
            }
            long compressedSize = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(sizesStart));
            long uncompressedSize = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(sizesStart + 8));
            entry.UpdateFromDataDescriptor(crc32, compressedSize, uncompressedSize);
        }
        else
        {
            int sizesStart = crc32Offset + 4;
            int totalNeeded = sizesStart + 8;
            if (bytesRead < totalNeeded)
            {
                await ReadExactlyAsync(buffer.AsMemory(bytesRead, totalNeeded - bytesRead), cancellationToken).ConfigureAwait(false);
            }
            uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sizesStart));
            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sizesStart + 4));
            entry.UpdateFromDataDescriptor(crc32, compressedSize, uncompressedSize);
        }
    }
}
