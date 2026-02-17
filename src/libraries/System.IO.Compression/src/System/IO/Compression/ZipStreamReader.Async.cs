// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Compression;

public sealed partial class ZipStreamReader
{
    /// <summary>
    /// Asynchronously advances the reader to the next entry in the archive.
    /// If an entry stream is currently open, it is drained and disposed before advancing.
    /// </summary>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns><see langword="true"/> if the reader successfully advanced to the next entry; <see langword="false"/> if there are no more entries.</returns>
    /// <exception cref="ObjectDisposedException">The reader has been disposed.</exception>
    /// <exception cref="InvalidDataException">The archive contains corrupt data and tolerant mode is not enabled.</exception>
    public async ValueTask<bool> MoveToNextEntryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await DrainCurrentEntryStreamAsync(cancellationToken).ConfigureAwait(false);

        ZipStreamReaderEntry? entry = await TryReadLocalFileHeaderAsync(cancellationToken).ConfigureAwait(false);
        if (entry is null)
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
    /// Asynchronously opens the current entry's data for reading as a decompressed stream.
    /// </summary>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="Stream"/> containing the decompressed entry data.</returns>
    /// <exception cref="InvalidOperationException">No current entry exists, or an entry stream is already open.</exception>
    /// <exception cref="NotSupportedException">The entry is encrypted or uses an unsupported compression method.</exception>
    public ValueTask<Stream> OpenEntryStreamAsync(CancellationToken cancellationToken = default)
    {
        // OpenEntryStream is not actually I/O-bound (it just creates wrapper streams),
        // so we can reuse the sync implementation.
        return new ValueTask<Stream>(OpenEntryStream());
    }

    private async ValueTask<ZipStreamReaderEntry?> TryReadLocalFileHeaderAsync(CancellationToken cancellationToken)
    {
        // Read the signature (4 bytes)
        byte[] signatureBuffer = new byte[ZipLocalFileHeader.FieldLengths.Signature];
        int bytesRead = await _archiveStream.ReadAtLeastAsync(signatureBuffer, signatureBuffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);

        if (bytesRead < signatureBuffer.Length)
        {
            return null;
        }

        // Check if this is a local file header signature
        if (!signatureBuffer.AsSpan().SequenceEqual(ZipLocalFileHeader.SignatureConstantBytes))
        {
            if (signatureBuffer[0] == 0x50 && signatureBuffer[1] == 0x4B)
            {
                return null;
            }

            if (!_tolerant)
            {
                throw new InvalidDataException(SR.LocalFileHeaderCorrupt);
            }

            return null;
        }

        // Read the rest of the fixed-size header (26 bytes after signature)
        int remainingHeaderSize = ZipLocalFileHeader.SizeOfLocalHeader - ZipLocalFileHeader.FieldLengths.Signature;
        byte[] headerBuffer = new byte[remainingHeaderSize];
        await _archiveStream.ReadExactlyAsync(headerBuffer, cancellationToken).ConfigureAwait(false);

        // Parse fields from the header buffer (offsets are relative to after signature)
        int offset = 0;
        ushort versionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(offset));
        offset += ZipLocalFileHeader.FieldLengths.VersionNeededToExtract;

        ushort bitFlags = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(offset));
        offset += ZipLocalFileHeader.FieldLengths.GeneralPurposeBitFlags;

        ushort compressionMethodValue = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(offset));
        offset += ZipLocalFileHeader.FieldLengths.CompressionMethod;

        uint lastModified = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.AsSpan(offset));
        offset += ZipLocalFileHeader.FieldLengths.LastModified;

        uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.AsSpan(offset));
        offset += ZipLocalFileHeader.FieldLengths.Crc32;

        uint compressedSizeSmall = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.AsSpan(offset));
        offset += ZipLocalFileHeader.FieldLengths.CompressedSize;

        uint uncompressedSizeSmall = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.AsSpan(offset));
        offset += ZipLocalFileHeader.FieldLengths.UncompressedSize;

        ushort filenameLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(offset));
        offset += ZipLocalFileHeader.FieldLengths.FilenameLength;

        ushort extraFieldLength = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(offset));

        // Read filename
        byte[] filenameBuffer = new byte[filenameLength];
        await _archiveStream.ReadExactlyAsync(filenameBuffer, cancellationToken).ConfigureAwait(false);

        bool isUtf8 = (bitFlags & (ushort)BitFlagValues.UnicodeFileNameAndComment) != 0;
        Encoding encoding = isUtf8 ? Encoding.UTF8 : (_entryNameEncoding ?? Encoding.UTF8);
        string fileName = encoding.GetString(filenameBuffer);

        // Read extra field
        byte[] extraFieldBuffer = new byte[extraFieldLength];
        await _archiveStream.ReadExactlyAsync(extraFieldBuffer, cancellationToken).ConfigureAwait(false);

        // Parse Zip64 extra field
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

        // Handle data descriptor flag
        bool hasDataDescriptor = (bitFlags & (ushort)BitFlagValues.DataDescriptor) != 0;
        bool isEncrypted = (bitFlags & (ushort)BitFlagValues.IsEncrypted) != 0;

        if (hasDataDescriptor)
        {
            compressedSize = -1;
            uncompressedSize = -1;
            crc32 = 0;
        }

        DateTimeOffset lastModifiedDate = new DateTimeOffset(ZipHelper.DosTimeToDateTime(lastModified));

        return new ZipStreamReaderEntry(
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
    }

    private async ValueTask DrainCurrentEntryStreamAsync(CancellationToken cancellationToken)
    {
        if (_currentEntryStream is not null)
        {
            await _currentEntryStream.DrainAndFinalizeAsync(cancellationToken).ConfigureAwait(false);
            await _currentEntryStream.DisposeAsync().ConfigureAwait(false);
            _currentEntryStream = null;
        }
        else if (_currentEntry is not null && !_currentEntry.IsFullyRead && !_currentEntry.HasDataDescriptor && _currentEntry.CompressedLength > 0)
        {
            await SkipBytesAsync(_archiveStream, _currentEntry.CompressedLength, cancellationToken).ConfigureAwait(false);
            _currentEntry.IsFullyRead = true;
        }
    }

    private static async ValueTask SkipBytesAsync(Stream stream, long count, CancellationToken cancellationToken)
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
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
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
}
