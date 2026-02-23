// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.IO.Compression;

/// <summary>
/// Represents the metadata for a single entry read from a ZIP archive's local file header
/// during forward-only streaming via <see cref="ZipInputStream"/>.
/// </summary>
public sealed class ZipInputStreamEntry
{
    internal ZipInputStreamEntry(
        string name,
        ZipCompressionMethod compressionMethod,
        DateTimeOffset lastModified,
        long compressedLength,
        long uncompressedLength,
        bool isEncrypted,
        bool hasDataDescriptor,
        ushort versionNeeded)
    {
        FullName = name;
        CompressionMethod = compressionMethod;
        LastModified = lastModified;
        CompressedLength = compressedLength;
        UncompressedLength = uncompressedLength;
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
    public string Name => Path.GetFileName(FullName);

    /// <summary>
    /// Gets the compression method used for this entry.
    /// </summary>
    public ZipCompressionMethod CompressionMethod { get; }

    /// <summary>
    /// Gets the last modified date and time of the entry.
    /// </summary>
    public DateTimeOffset LastModified { get; }

    /// <summary>
    /// Gets the compressed size of the entry in bytes.
    /// Returns -1 if the size is not yet known (data descriptor entries that have not been fully read).
    /// </summary>
    public long CompressedLength { get; }

    /// <summary>
    /// Gets the uncompressed size of the entry in bytes.
    /// Returns -1 if the size is not yet known (data descriptor entries that have not been fully read).
    /// </summary>
    public long UncompressedLength { get; }

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
    /// Gets a value indicating whether this entry uses a data descriptor.
    /// </summary>
    internal bool HasDataDescriptor { get; }
}
