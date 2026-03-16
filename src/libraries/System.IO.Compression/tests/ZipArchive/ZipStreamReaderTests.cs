// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.IO.Compression.Tests;

public class ZipStreamReaderTests
{
    [Fact]
    public void Constructor_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>("stream", () => new ZipStreamReader(null!));
    }

    [Fact]
    public void Constructor_UnreadableStream_Throws()
    {
        using var writeOnly = new MemoryStream(Array.Empty<byte>(), writable: false);
        var wrapper = new WriteOnlyStreamWrapper(writeOnly);
        Assert.Throws<ArgumentException>("stream", () => new ZipStreamReader(wrapper));
    }

    [Fact]
    public void GetNextEntry_Disposed_Throws()
    {
        var reader = new ZipStreamReader(new MemoryStream());
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.GetNextEntry());
    }

    [Fact]
    public async Task GetNextEntryAsync_Disposed_Throws()
    {
        var reader = new ZipStreamReader(new MemoryStream());
        reader.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await reader.GetNextEntryAsync());
    }

    [Fact]
    public void EmptyArchive_ReturnsNull()
    {
        byte[] zipBytes = CreateMinimalZipArchive();
        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public async Task EmptyArchive_Async_ReturnsNull()
    {
        byte[] zipBytes = CreateMinimalZipArchive();
        await using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        Assert.Null(await reader.GetNextEntryAsync());
    }

    [Fact]
    public void ReadSingleStoredEntry()
    {
        byte[] content = "Hello, World!"u8.ToArray();
        byte[] zipBytes = CreateZipWithSingleEntry("test.txt", content, CompressionLevel.NoCompression);

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        var entry = reader.GetNextEntry();

        Assert.NotNull(entry);
        Assert.Equal("test.txt", entry.FullName);
        Assert.Equal("test.txt", entry.Name);
        Assert.Equal(ZipCompressionMethod.Stored, entry.CompressionMethod);
        Assert.False(entry.IsDirectory);
        Assert.False(entry.IsEncrypted);
        Assert.NotNull(entry.DataStream);

        byte[] readContent = new byte[content.Length];
        int totalRead = 0;
        int read;
        while ((read = entry.DataStream.Read(readContent, totalRead, readContent.Length - totalRead)) > 0)
        {
            totalRead += read;
        }
        Assert.Equal(content.Length, totalRead);
        Assert.Equal(content, readContent);

        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public void ReadSingleDeflateEntry()
    {
        byte[] content = Encoding.UTF8.GetBytes("This is a test of deflate compression. " +
            "Adding more text to make deflate actually compress something meaningful.");
        byte[] zipBytes = CreateZipWithSingleEntry("compressed.txt", content, CompressionLevel.Optimal);

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        var entry = reader.GetNextEntry();

        Assert.NotNull(entry);
        Assert.Equal("compressed.txt", entry.FullName);
        Assert.Equal(ZipCompressionMethod.Deflate, entry.CompressionMethod);
        Assert.NotNull(entry.DataStream);

        using var ms = new MemoryStream();
        entry.DataStream.CopyTo(ms);
        Assert.Equal(content, ms.ToArray());

        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public async Task ReadSingleDeflateEntry_Async()
    {
        byte[] content = Encoding.UTF8.GetBytes("Async test data for deflate compression.");
        byte[] zipBytes = CreateZipWithSingleEntry("async.txt", content, CompressionLevel.Fastest);

        await using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        var entry = await reader.GetNextEntryAsync();

        Assert.NotNull(entry);
        Assert.Equal("async.txt", entry.FullName);
        Assert.NotNull(entry.DataStream);

        using var ms = new MemoryStream();
        await entry.DataStream.CopyToAsync(ms);
        Assert.Equal(content, ms.ToArray());

        Assert.Null(await reader.GetNextEntryAsync());
    }

    [Fact]
    public void ReadMultipleEntries()
    {
        byte[] zipBytes = CreateZipWithMultipleEntries();

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));

        var entry1 = reader.GetNextEntry();
        Assert.NotNull(entry1);
        Assert.Equal("file1.txt", entry1.FullName);
        using (var ms1 = new MemoryStream())
        {
            entry1.DataStream!.CopyTo(ms1);
            Assert.Equal("Content of file 1", Encoding.UTF8.GetString(ms1.ToArray()));
        }

        var entry2 = reader.GetNextEntry();
        Assert.NotNull(entry2);
        Assert.Equal("file2.txt", entry2.FullName);
        using (var ms2 = new MemoryStream())
        {
            entry2.DataStream!.CopyTo(ms2);
            Assert.Equal("Content of file 2", Encoding.UTF8.GetString(ms2.ToArray()));
        }

        var entry3 = reader.GetNextEntry();
        Assert.NotNull(entry3);
        Assert.Equal("subdir/file3.txt", entry3.FullName);
        Assert.Equal("file3.txt", entry3.Name);

        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public void ReadDirectoryEntry()
    {
        byte[] zipBytes = CreateZipWithDirectory();

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));

        var dirEntry = reader.GetNextEntry();
        Assert.NotNull(dirEntry);
        Assert.True(dirEntry.IsDirectory);
        Assert.Null(dirEntry.DataStream);

        var fileEntry = reader.GetNextEntry();
        Assert.NotNull(fileEntry);
        Assert.False(fileEntry.IsDirectory);
        Assert.NotNull(fileEntry.DataStream);
    }

    [Fact]
    public void ReadWithCopyData_PreservesStream()
    {
        byte[] content = "Preserved content"u8.ToArray();
        byte[] zipBytes = CreateZipWithSingleEntry("preserved.txt", content, CompressionLevel.NoCompression);

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        var entry = reader.GetNextEntry(copyData: true);

        Assert.NotNull(entry);
        Assert.NotNull(entry.DataStream);
        Assert.IsType<MemoryStream>(entry.DataStream);

        // Advance past this entry
        Assert.Null(reader.GetNextEntry());

        // DataStream should still be valid since we used copyData: true
        // However, our implementation invalidates it on advance. The MemoryStream itself
        // still works because it's a copy.
        using var ms = new MemoryStream();
        entry.DataStream.Position = 0;
        entry.DataStream.CopyTo(ms);
        Assert.Equal(content, ms.ToArray());
    }

    [Fact]
    public void NonSeekableStream_WorksCorrectly()
    {
        byte[] content = "Non-seekable test"u8.ToArray();
        byte[] zipBytes = CreateZipWithSingleEntry("test.txt", content, CompressionLevel.NoCompression);

        using var nonSeekable = new ForwardOnlyStream(new MemoryStream(zipBytes));
        using var reader = new ZipStreamReader(nonSeekable);
        var entry = reader.GetNextEntry();

        Assert.NotNull(entry);
        using var ms = new MemoryStream();
        entry.DataStream!.CopyTo(ms);
        Assert.Equal(content, ms.ToArray());
    }

    [Fact]
    public void AdvancingInvalidatesPreviousEntry()
    {
        byte[] zipBytes = CreateZipWithMultipleEntries();

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));

        var entry1 = reader.GetNextEntry();
        Assert.NotNull(entry1);
        Assert.NotNull(entry1.DataStream);

        // Don't read entry1's data, advance to entry2
        var entry2 = reader.GetNextEntry();
        Assert.NotNull(entry2);

        // entry1's DataStream should be invalidated
        Assert.Null(entry1.DataStream);
    }

    [Fact]
    public void DisposeClosesStream_WhenLeaveOpenFalse()
    {
        var ms = new MemoryStream(CreateMinimalZipArchive());
        var reader = new ZipStreamReader(ms, leaveOpen: false);
        reader.Dispose();

        Assert.False(ms.CanRead);
    }

    [Fact]
    public void DisposeKeepsStreamOpen_WhenLeaveOpenTrue()
    {
        var ms = new MemoryStream(CreateMinimalZipArchive());
        var reader = new ZipStreamReader(ms, leaveOpen: true);
        reader.Dispose();

        Assert.True(ms.CanRead);
    }

    [Fact]
    public async Task DisposeAsync_ClosesStream_WhenLeaveOpenFalse()
    {
        var ms = new MemoryStream(CreateMinimalZipArchive());
        var reader = new ZipStreamReader(ms, leaveOpen: false);
        await reader.DisposeAsync();

        Assert.False(ms.CanRead);
    }

    [Fact]
    public void EntryMetadata_Correct()
    {
        byte[] content = "Test data"u8.ToArray();
        byte[] zipBytes = CreateZipWithSingleEntry("metadata.txt", content, CompressionLevel.NoCompression);

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        var entry = reader.GetNextEntry();

        Assert.NotNull(entry);
        Assert.Equal("metadata.txt", entry.FullName);
        Assert.Equal("metadata.txt", entry.Name);
        Assert.False(entry.IsEncrypted);
        Assert.False(entry.IsDirectory);
        Assert.True(entry.Length >= 0);
        Assert.True(entry.CompressedLength >= 0);
        Assert.True(entry.VersionNeeded > 0);
    }

    [Fact]
    public void ReadEntryWithSubdirectoryPath()
    {
        byte[] content = "In a subdirectory"u8.ToArray();
        byte[] zipBytes = CreateZipWithSingleEntry("dir1/dir2/deep.txt", content, CompressionLevel.NoCompression);

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        var entry = reader.GetNextEntry();

        Assert.NotNull(entry);
        Assert.Equal("dir1/dir2/deep.txt", entry.FullName);
        Assert.Equal("deep.txt", entry.Name);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void ExtractToFile_WritesContent()
    {
        byte[] content = "Extract me!"u8.ToArray();
        byte[] zipBytes = CreateZipWithSingleEntry("extract.txt", content, CompressionLevel.NoCompression);

        string tempFile = Path.GetTempFileName();
        try
        {
            using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
            var entry = reader.GetNextEntry();
            Assert.NotNull(entry);

            entry.ExtractToFile(tempFile, overwrite: true);
            Assert.Equal(content, File.ReadAllBytes(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExtractToFileAsync_WritesContent()
    {
        byte[] content = "Extract me async!"u8.ToArray();
        byte[] zipBytes = CreateZipWithSingleEntry("extract_async.txt", content, CompressionLevel.NoCompression);

        string tempFile = Path.GetTempFileName();
        try
        {
            await using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
            var entry = await reader.GetNextEntryAsync();
            Assert.NotNull(entry);

            await entry.ExtractToFileAsync(tempFile, overwrite: true);
            Assert.Equal(content, await File.ReadAllBytesAsync(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ExtractToFile_DirectoryEntry_Throws()
    {
        byte[] zipBytes = CreateZipWithDirectory();

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        var entry = reader.GetNextEntry(); // directory
        Assert.NotNull(entry);
        Assert.True(entry.IsDirectory);

        Assert.Throws<InvalidOperationException>(() => entry.ExtractToFile("somefile.txt", overwrite: true));
    }

    [Fact]
    public void CorruptSignature_ThrowsInvalidDataException()
    {
        byte[] data = [0xFF, 0xFF, 0xFF, 0xFF]; // Not a ZIP signature
        using var reader = new ZipStreamReader(new MemoryStream(data));
        Assert.Throws<InvalidDataException>(() => reader.GetNextEntry());
    }

    [Fact]
    public void TruncatedStream_ThrowsEndOfStreamException()
    {
        byte[] data = [0x50, 0x4B]; // Partial PK signature
        using var reader = new ZipStreamReader(new MemoryStream(data));
        Assert.Throws<EndOfStreamException>(() => reader.GetNextEntry());
    }

    [Fact]
    public void EmptyStream_ThrowsEndOfStreamException()
    {
        using var reader = new ZipStreamReader(new MemoryStream());
        Assert.Throws<EndOfStreamException>(() => reader.GetNextEntry());
    }

    [Fact]
    public void CentralDirectorySignature_ReturnsNull()
    {
        // Central directory header signature (PK\x01\x02)
        byte[] data = [0x50, 0x4B, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00];
        using var reader = new ZipStreamReader(new MemoryStream(data));
        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public void ReadWithCustomEncoding()
    {
        byte[] content = "Content"u8.ToArray();
        byte[] zipBytes = CreateZipWithSingleEntry("test.txt", content, CompressionLevel.NoCompression);

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes), Encoding.UTF8, leaveOpen: false);
        var entry = reader.GetNextEntry();

        Assert.NotNull(entry);
        Assert.Equal("test.txt", entry.FullName);
    }

    [Fact]
    public void ReadMultipleEntries_WithCopyData()
    {
        byte[] zipBytes = CreateZipWithMultipleEntries();

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        var entries = new System.Collections.Generic.List<ZipStreamReaderEntry>();

        while (reader.GetNextEntry(copyData: true) is { } entry)
        {
            entries.Add(entry);
        }

        Assert.Equal(3, entries.Count);
        Assert.Equal("file1.txt", entries[0].FullName);
        Assert.Equal("file2.txt", entries[1].FullName);
        Assert.Equal("subdir/file3.txt", entries[2].FullName);

        // Verify data is still accessible (since copyData was true, DataStreams are MemoryStreams)
        foreach (var entry in entries)
        {
            if (entry.DataStream is not null)
            {
                entry.DataStream.Position = 0;
                using var ms = new MemoryStream();
                entry.DataStream.CopyTo(ms);
                Assert.True(ms.Length > 0);
            }
        }
    }

    [Fact]
    public void SkippingEntryData_StillAllowsNextEntry()
    {
        byte[] zipBytes = CreateZipWithMultipleEntries();

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));

        var entry1 = reader.GetNextEntry();
        Assert.NotNull(entry1);
        // Intentionally don't read entry1's data

        var entry2 = reader.GetNextEntry();
        Assert.NotNull(entry2);
        Assert.Equal("file2.txt", entry2.FullName);

        using var ms = new MemoryStream();
        entry2.DataStream!.CopyTo(ms);
        Assert.Equal("Content of file 2", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task ReadMultipleEntries_Async()
    {
        byte[] zipBytes = CreateZipWithMultipleEntries();

        await using var reader = new ZipStreamReader(new MemoryStream(zipBytes));

        var entry1 = await reader.GetNextEntryAsync();
        Assert.NotNull(entry1);
        Assert.Equal("file1.txt", entry1.FullName);

        using var ms1 = new MemoryStream();
        await entry1.DataStream!.CopyToAsync(ms1);
        Assert.Equal("Content of file 1", Encoding.UTF8.GetString(ms1.ToArray()));

        var entry2 = await reader.GetNextEntryAsync();
        Assert.NotNull(entry2);
        Assert.Equal("file2.txt", entry2.FullName);

        Assert.NotNull(await reader.GetNextEntryAsync()); // entry3
        Assert.Null(await reader.GetNextEntryAsync()); // end
    }

    [Fact]
    public void ReadZipCreatedByZipArchive()
    {
        var entries = new (string Name, byte[] Content)[]
        {
            ("readme.txt", Encoding.UTF8.GetBytes("This is a readme file.")),
            ("data/values.csv", Encoding.UTF8.GetBytes("a,b,c\n1,2,3\n4,5,6")),
            ("empty.txt", Array.Empty<byte>()),
        };

        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        zipStream.Position = 0;

        using var reader = new ZipStreamReader(zipStream);
        int index = 0;
        while (reader.GetNextEntry() is { } entry)
        {
            Assert.Equal(entries[index].Name, entry.FullName);

            if (entry.DataStream is not null)
            {
                using var ms = new MemoryStream();
                entry.DataStream.CopyTo(ms);
                Assert.Equal(entries[index].Content, ms.ToArray());
            }

            index++;
        }

        Assert.Equal(entries.Length, index);
    }

    [Fact]
    public async Task ReadZipCreatedByZipArchive_Async()
    {
        var entries = new (string Name, byte[] Content)[]
        {
            ("file1.txt", Encoding.UTF8.GetBytes("Async test content 1")),
            ("file2.txt", Encoding.UTF8.GetBytes("Async test content 2")),
        };

        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        zipStream.Position = 0;

        await using var reader = new ZipStreamReader(zipStream);
        int index = 0;
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            Assert.Equal(entries[index].Name, entry.FullName);

            using var ms = new MemoryStream();
            await entry.DataStream!.CopyToAsync(ms);
            Assert.Equal(entries[index].Content, ms.ToArray());

            index++;
        }

        Assert.Equal(entries.Length, index);
    }

    [Fact]
    public void ReadZipWithLargeStoredEntry()
    {
        byte[] content = new byte[100_000];
        Random.Shared.NextBytes(content);
        byte[] zipBytes = CreateZipWithSingleEntry("large.bin", content, CompressionLevel.NoCompression);

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        var entry = reader.GetNextEntry();

        Assert.NotNull(entry);
        using var ms = new MemoryStream();
        entry.DataStream!.CopyTo(ms);
        Assert.Equal(content, ms.ToArray());
    }

    [Fact]
    public void ReadZipWithLargeDeflateEntry()
    {
        byte[] content = new byte[100_000];
        Random.Shared.NextBytes(content);
        byte[] zipBytes = CreateZipWithSingleEntry("large_compressed.bin", content, CompressionLevel.Fastest);

        using var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        var entry = reader.GetNextEntry();

        Assert.NotNull(entry);
        using var ms = new MemoryStream();
        entry.DataStream!.CopyTo(ms);
        Assert.Equal(content, ms.ToArray());
    }

    [Fact]
    public void NonSeekableStream_MultipleEntries()
    {
        byte[] zipBytes = CreateZipWithMultipleEntries();

        using var nonSeekable = new ForwardOnlyStream(new MemoryStream(zipBytes));
        using var reader = new ZipStreamReader(nonSeekable);

        var entry1 = reader.GetNextEntry();
        Assert.NotNull(entry1);
        using (var ms1 = new MemoryStream())
        {
            entry1.DataStream!.CopyTo(ms1);
            Assert.Equal("Content of file 1", Encoding.UTF8.GetString(ms1.ToArray()));
        }

        var entry2 = reader.GetNextEntry();
        Assert.NotNull(entry2);
        using (var ms2 = new MemoryStream())
        {
            entry2.DataStream!.CopyTo(ms2);
            Assert.Equal("Content of file 2", Encoding.UTF8.GetString(ms2.ToArray()));
        }

        Assert.NotNull(reader.GetNextEntry()); // entry3
        Assert.Null(reader.GetNextEntry()); // end
    }

    [Fact]
    public void GetNextEntry_CancellationToken_Cancelled()
    {
        byte[] zipBytes = CreateMinimalZipArchive();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var reader = new ZipStreamReader(new MemoryStream(zipBytes));
        Assert.True(reader.GetNextEntryAsync(cancellationToken: cts.Token).IsCanceled);
        reader.Dispose();
    }

    // Helper methods to create ZIP archives for testing

    private static byte[] CreateMinimalZipArchive()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Empty archive
        }
        return ms.ToArray();
    }

    private static byte[] CreateZipWithSingleEntry(string name, byte[] content, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(name, level);
            using var entryStream = entry.Open();
            entryStream.Write(content);
        }
        return ms.ToArray();
    }

    private static byte[] CreateZipWithMultipleEntries()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e1 = archive.CreateEntry("file1.txt", CompressionLevel.NoCompression);
            using (var s1 = e1.Open()) s1.Write("Content of file 1"u8);

            var e2 = archive.CreateEntry("file2.txt", CompressionLevel.Optimal);
            using (var s2 = e2.Open()) s2.Write("Content of file 2"u8);

            var e3 = archive.CreateEntry("subdir/file3.txt", CompressionLevel.Fastest);
            using (var s3 = e3.Open()) s3.Write("Content of file 3 in a subdirectory"u8);
        }
        return ms.ToArray();
    }

    private static byte[] CreateZipWithDirectory()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("mydir/");
            var e = archive.CreateEntry("mydir/file.txt", CompressionLevel.NoCompression);
            using var s = e.Open();
            s.Write("File in directory"u8);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// A stream wrapper that prevents seeking, simulating a non-seekable stream.
    /// </summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly Stream _inner;

        public ForwardOnlyStream(Stream inner) => _inner = inner;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A stream wrapper that only supports writing (not reading), for testing constructor validation.
    /// </summary>
    private sealed class WriteOnlyStreamWrapper : Stream
    {
        private readonly Stream _inner;
        public WriteOnlyStreamWrapper(Stream inner) => _inner = inner;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }
}
