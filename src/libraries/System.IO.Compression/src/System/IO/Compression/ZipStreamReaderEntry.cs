// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.IO.Compression;

/// <summary>
/// Represents the metadata for a single entry read from a ZIP archive's local file header
/// during forward-only streaming.
/// </summary>
public sealed class ZipStreamReaderEntry
{
    private uint _crc32;
    private long _compressedLength;
    private long _uncompressedLength;

    internal ZipStreamReaderEntry(
        string name,
        ZipCompressionMethod compressionMethod,
        DateTimeOffset lastModified,
        uint crc32,
        long compressedLength,
        long uncompressedLength,
        ushort generalPurposeBitFlags,
        bool isEncrypted,
        bool hasDataDescriptor,
        ushort versionNeeded)
    {
        FullName = name;
        CompressionMethod = compressionMethod;
        LastModified = lastModified;
        _crc32 = crc32;
        _compressedLength = compressedLength;
        _uncompressedLength = uncompressedLength;
        GeneralPurposeBitFlags = generalPurposeBitFlags;
        IsEncrypted = isEncrypted;
        HasDataDescriptor = hasDataDescriptor;
        VersionNeeded = versionNeeded;
    }

    /// <summary>
    /// Gets the full name (path) of the entry within the archive.
    /// </summary>
    public string FullName { get; }

    /// <summary>
    /// Gets the file name portion of the entry (after the last directory separator).
    /// </summary>
    public string Name => IO.Path.GetFileName(FullName);

    /// <summary>
    /// Gets the compression method used for this entry.
    /// </summary>
    public ZipCompressionMethod CompressionMethod { get; }

    /// <summary>
    /// Gets the last modified date and time of the entry.
    /// </summary>
    public DateTimeOffset LastModified { get; }

    /// <summary>
    /// Gets the CRC-32 checksum of the uncompressed entry data.
    /// For entries with data descriptors, this value is updated after the entry stream is fully read.
    /// </summary>
    [CLSCompliant(false)]
    public uint Crc32 => _crc32;

    /// <summary>
    /// Gets the compressed size of the entry in bytes.
    /// Returns -1 if the size is not yet known (data descriptor entries that have not been fully read).
    /// </summary>
    public long CompressedLength => _compressedLength;

    /// <summary>
    /// Gets the uncompressed size of the entry in bytes.
    /// Returns -1 if the size is not yet known (data descriptor entries that have not been fully read).
    /// </summary>
    public long UncompressedLength => _uncompressedLength;

    /// <summary>
    /// Gets the general purpose bit flags from the local file header.
    /// </summary>
    [CLSCompliant(false)]
    public ushort GeneralPurposeBitFlags { get; }

    /// <summary>
    /// Gets a value indicating whether the entry is encrypted.
    /// </summary>
    public bool IsEncrypted { get; }

    /// <summary>
    /// Gets a value indicating whether the entry name ends with a directory separator,
    /// which conventionally indicates a directory entry.
    /// </summary>
    public bool IsDirectory => FullName.EndsWith('/');

    /// <summary>
    /// Gets the minimum ZIP specification version needed to extract this entry.
    /// </summary>
    [CLSCompliant(false)]
    public ushort VersionNeeded { get; }

    /// <summary>
    /// Gets a value indicating whether this entry uses a data descriptor
    /// (CRC-32 and sizes appear after the entry data rather than in the local header).
    /// </summary>
    internal bool HasDataDescriptor { get; }

    /// <summary>
    /// Gets or sets the CRC-32 computed during streaming decompression.
    /// </summary>
    internal uint ComputedCrc32 { get; set; }

    /// <summary>
    /// Gets or sets the total uncompressed bytes read from the entry stream.
    /// </summary>
    internal long ComputedUncompressedLength { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the entry stream has been fully consumed.
    /// </summary>
    internal bool IsFullyRead { get; set; }

    /// <summary>
    /// Updates the entry metadata from a data descriptor read after the entry data.
    /// </summary>
    internal void UpdateFromDataDescriptor(uint crc32, long compressedSize, long uncompressedSize)
    {
        _crc32 = crc32;
        _compressedLength = compressedSize;
        _uncompressedLength = uncompressedSize;
    }

    /// <summary>
    /// Validates the integrity of the entry by comparing the CRC-32 computed during reading
    /// against the CRC-32 stored in the local header or data descriptor.
    /// </summary>
    /// <returns><see langword="true"/> if the entry data integrity is valid; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="InvalidOperationException">The entry stream has not been fully read yet.</exception>
    public bool ValidateEntry()
    {
        return ValidateEntry(out _);
    }

    /// <summary>
    /// Validates the integrity of the entry by comparing the CRC-32 computed during reading
    /// against the CRC-32 stored in the local header or data descriptor.
    /// </summary>
    /// <param name="errorMessage">When this method returns <see langword="false"/>, contains a description of the validation failure.</param>
    /// <returns><see langword="true"/> if the entry data integrity is valid; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="InvalidOperationException">The entry stream has not been fully read yet.</exception>
    public bool ValidateEntry(out string? errorMessage)
    {
        if (!IsFullyRead)
        {
            throw new InvalidOperationException(SR.ZipStreamReaderEntryNotFullyRead);
        }

        if (ComputedCrc32 != _crc32)
        {
            errorMessage = SR.Format(SR.ZipStreamReaderCrc32Mismatch, ComputedCrc32, _crc32);
            return false;
        }

        if (_uncompressedLength >= 0 && ComputedUncompressedLength != _uncompressedLength)
        {
            errorMessage = SR.Format(SR.ZipStreamReaderSizeMismatch, ComputedUncompressedLength, _uncompressedLength);
            return false;
        }

        errorMessage = null;
        return true;
    }
}
