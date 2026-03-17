// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Compression;

/// <summary>
/// Represents an entry in a ZIP archive read by <see cref="ZipStreamReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each entry provides metadata from the local file header and a
/// <see cref="DataStream"/> that contains the decompressed data.
/// </para>
/// <para>
/// When <see cref="ZipStreamReader.GetNextEntry(bool)"/> is called with
/// <c>copyData: false</c> (the default), the <see cref="DataStream"/> is
/// invalidated when the reader advances to the next entry. To preserve the
/// data, pass <c>copyData: true</c> to copy it into a self-contained
/// <see cref="MemoryStream"/>.
/// </para>
/// </remarks>
public sealed class ZipStreamReaderEntry
{
    private Stream? _dataStream;
    private ZipBoundedReadStream? _boundedStream;
    private bool _dataStreamInvalidated;
    private readonly bool _hasDataDescriptor;
    private readonly bool _dataCopied;
    private uint _crc32;
    private long _compressedLength;
    private long _length;

    internal ZipStreamReaderEntry(
        string fullName,
        ZipCompressionMethod compressionMethod,
        DateTimeOffset lastModified,
        uint crc32,
        long compressedLength,
        long length,
        ushort generalPurposeBitFlags,
        ushort versionNeeded,
        Stream? dataStream,
        bool hasDataDescriptor,
        ZipBoundedReadStream? boundedStream = null,
        bool dataCopied = false)
    {
        FullName = fullName;
        CompressionMethod = compressionMethod;
        LastModified = lastModified;
        _crc32 = crc32;
        _compressedLength = compressedLength;
        _length = length;
        GeneralPurposeBitFlags = generalPurposeBitFlags;
        VersionNeeded = versionNeeded;
        _dataStream = dataStream;
        _hasDataDescriptor = hasDataDescriptor;
        _boundedStream = boundedStream;
        _dataCopied = dataCopied;
    }

    /// <summary>
    /// Gets the full name (relative path) of the entry, including any directory path.
    /// </summary>
    public string FullName { get; }

    /// <summary>
    /// Gets the file name portion of the entry (the part after the last directory separator).
    /// </summary>
    public string Name => Path.GetFileName(FullName);

    /// <summary>
    /// Gets the compression method used for this entry.
    /// </summary>
    public ZipCompressionMethod CompressionMethod { get; }

    /// <summary>
    /// Gets the last modification date and time of the entry.
    /// </summary>
    public DateTimeOffset LastModified { get; }

    /// <summary>
    /// Gets the CRC-32 checksum of the uncompressed data.
    /// </summary>
    /// <remarks>
    /// When bit 3 (data descriptor) is set in the local header, this value is initially
    /// zero and is populated after the <see cref="DataStream"/> has been fully read.
    /// </remarks>
    [CLSCompliant(false)]
    public uint Crc32 => _crc32;

    /// <summary>
    /// Gets the compressed size of the entry in bytes.
    /// </summary>
    /// <remarks>
    /// When bit 3 (data descriptor) is set in the local header, this value is initially
    /// zero and is populated after the <see cref="DataStream"/> has been fully read.
    /// </remarks>
    public long CompressedLength => _compressedLength;

    /// <summary>
    /// Gets the uncompressed size of the entry in bytes.
    /// </summary>
    /// <remarks>
    /// When bit 3 (data descriptor) is set in the local header, this value is initially
    /// zero and is populated after the <see cref="DataStream"/> has been fully read.
    /// </remarks>
    public long Length => _length;

    /// <summary>
    /// Gets the raw general purpose bit flags from the local file header.
    /// </summary>
    [CLSCompliant(false)]
    public ushort GeneralPurposeBitFlags { get; }

    /// <summary>
    /// Gets a value indicating whether the entry is encrypted.
    /// </summary>
    public bool IsEncrypted => (GeneralPurposeBitFlags & 1) != 0;

    /// <summary>
    /// Gets a value indicating whether the entry represents a directory.
    /// </summary>
    public bool IsDirectory => FullName.Length > 0 && (FullName[^1] is '/' or '\\');

    /// <summary>
    /// Gets the minimum ZIP specification version needed to extract this entry.
    /// </summary>
    [CLSCompliant(false)]
    public ushort VersionNeeded { get; }

    /// <summary>
    /// Gets the decompressed data stream for this entry.
    /// </summary>
    /// <value>
    /// A <see cref="Stream"/> containing the decompressed data, or <see langword="null"/>
    /// if the entry is a directory.
    /// When <c>copyData</c> is <see langword="false"/>, the stream is invalidated when
    /// <see cref="ZipStreamReader.GetNextEntry(bool)"/> is called again.
    /// When <c>copyData</c> is <see langword="true"/>, the stream is a
    /// <see cref="MemoryStream"/> that remains valid independently.
    /// </value>
    public Stream? DataStream
    {
        get
        {
            if (_dataStreamInvalidated && !_dataCopied)
            {
                return null;
            }
            return _dataStream;
        }
    }

    /// <summary>
    /// Extracts the entry to the specified file.
    /// </summary>
    /// <param name="destinationFileName">The path of the destination file.</param>
    /// <param name="overwrite">
    /// <see langword="true"/> to overwrite an existing file; otherwise, <see langword="false"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="destinationFileName"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="destinationFileName"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The entry is a directory or the data stream has been invalidated.</exception>
    public void ExtractToFile(string destinationFileName, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationFileName);

        if (IsDirectory)
        {
            throw new InvalidOperationException(SR.ZipStreamReaderCannotExtractDirectory);
        }

        if (_dataStreamInvalidated || _dataStream is null)
        {
            throw new InvalidOperationException(SR.ZipStreamReaderDataStreamInvalidated);
        }

        FileMode fileMode = overwrite ? FileMode.Create : FileMode.CreateNew;
        using FileStream fileStream = new FileStream(destinationFileName, fileMode, FileAccess.Write, FileShare.None);
        _dataStream.CopyTo(fileStream);
    }

    /// <summary>
    /// Asynchronously extracts the entry to the specified file.
    /// </summary>
    /// <param name="destinationFileName">The path of the destination file.</param>
    /// <param name="overwrite">
    /// <see langword="true"/> to overwrite an existing file; otherwise, <see langword="false"/>.
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous extraction operation.</returns>
    /// <exception cref="ArgumentException"><paramref name="destinationFileName"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="destinationFileName"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The entry is a directory or the data stream has been invalidated.</exception>
    public async Task ExtractToFileAsync(string destinationFileName, bool overwrite, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationFileName);

        if (IsDirectory)
        {
            throw new InvalidOperationException(SR.ZipStreamReaderCannotExtractDirectory);
        }

        if (_dataStreamInvalidated || _dataStream is null)
        {
            throw new InvalidOperationException(SR.ZipStreamReaderDataStreamInvalidated);
        }

        FileMode fileMode = overwrite ? FileMode.Create : FileMode.CreateNew;
        FileStream fileStream = new FileStream(destinationFileName, fileMode, FileAccess.Write, FileShare.None);
        await using (fileStream.ConfigureAwait(false))
        {
            await _dataStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }
    }

    internal void InvalidateDataStream()
    {
        if (_dataStreamInvalidated)
        {
            return;
        }

        _dataStreamInvalidated = true;

        _boundedStream?.Dispose();
        _boundedStream = null;

        // Don't dispose copied (MemoryStream) data streams here — they are owned
        // by the caller and tracked by ZipStreamReader._dataStreamsToDispose for
        // disposal when the reader itself is disposed.
        if (!_dataCopied)
        {
            _dataStream?.Dispose();
            _dataStream = null;
        }
    }

    internal void UpdateFromDataDescriptor(uint crc32, long compressedSize, long uncompressedSize)
    {
        _crc32 = crc32;
        _compressedLength = compressedSize;
        _length = uncompressedSize;
    }

    internal void AdvancePastData(ZipStreamReader reader)
    {
        if (_dataStream is null)
        {
            return;
        }

        if (_dataStream is MemoryStream)
        {
            // Already copied; drain remaining compressed bytes from the bounded stream
            // (CopyTo may not have consumed all compressed bytes from the archive).
            _boundedStream?.DrainRemaining();
            InvalidateDataStream();
            return;
        }

        if (_dataStream is ZipForwardOnlyDeflateStream deflateStream)
        {
            deflateStream.DrainToEnd();
            InvalidateDataStream();
            return;
        }

        byte[] drainBuffer = new byte[4096];
        while (_dataStream.Read(drainBuffer, 0, drainBuffer.Length) > 0) { }

        // After draining decompressed data, the underlying BoundedReadStream may still
        // have remaining compressed bytes (e.g., DeflateStream self-terminates before
        // consuming all bounded bytes). Drain those too.
        _boundedStream?.DrainRemaining();

        if (_hasDataDescriptor)
        {
            reader.ReadDataDescriptor(this);
        }

        InvalidateDataStream();
    }

    internal async ValueTask AdvancePastDataAsync(ZipStreamReader reader, CancellationToken cancellationToken)
    {
        if (_dataStream is null)
        {
            return;
        }

        if (_dataStream is MemoryStream)
        {
            if (_boundedStream is not null)
            {
                await _boundedStream.DrainRemainingAsync(cancellationToken).ConfigureAwait(false);
            }
            InvalidateDataStream();
            return;
        }

        if (_dataStream is ZipForwardOnlyDeflateStream deflateStream)
        {
            await deflateStream.DrainToEndAsync(cancellationToken).ConfigureAwait(false);
            InvalidateDataStream();
            return;
        }

        byte[] drainBuffer = new byte[4096];
        while (await _dataStream.ReadAsync(drainBuffer, cancellationToken).ConfigureAwait(false) > 0) { }

        if (_boundedStream is not null)
        {
            await _boundedStream.DrainRemainingAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_hasDataDescriptor)
        {
            await reader.ReadDataDescriptorAsync(this, cancellationToken).ConfigureAwait(false);
        }

        InvalidateDataStream();
    }
}
