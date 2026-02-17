// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO.Compression.Tests.Utilities;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.IO.Compression.Tests
{
    public class ZipStreamReaderTests : ZipFileTestBase
    {

        [Theory]
        [InlineData("normal.zip")]
        [InlineData("small.zip")]
        [InlineData("empty.zip")]
        [InlineData("emptydir.zip")]
        [InlineData("unicode.zip")]
        public async Task ReadEntries_MatchesZipArchive(string zipFile)
        {
            string path = zfile(zipFile);

            // Get expected entries from ZipArchive
            var expectedEntries = new List<(string Name, long Length, ZipCompressionMethod Method)>();
            using (var archiveStream = await StreamHelpers.CreateTempCopyStream(path))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    expectedEntries.Add((entry.FullName, entry.Length, entry.CompressionMethod));
                }
            }

            // Read using ZipStreamReader
            var actualEntries = new List<(string Name, long Length, ZipCompressionMethod Method)>();
            using (var stream = await StreamHelpers.CreateTempCopyStream(path))
            using (var reader = new ZipStreamReader(stream))
            {
                while (reader.MoveToNextEntry())
                {
                    var entry = reader.CurrentEntry!;
                    using Stream entryStream = reader.OpenEntryStream();

                    long bytesRead = 0;
                    byte[] buffer = new byte[4096];
                    int read;
                    while ((read = entryStream.Read(buffer)) > 0)
                    {
                        bytesRead += read;
                    }

                    actualEntries.Add((entry.FullName, bytesRead, entry.CompressionMethod));
                }
            }

            Assert.Equal(expectedEntries.Count, actualEntries.Count);
            for (int i = 0; i < expectedEntries.Count; i++)
            {
                Assert.Equal(expectedEntries[i].Name, actualEntries[i].Name);
                Assert.Equal(expectedEntries[i].Length, actualEntries[i].Length);
                Assert.Equal(expectedEntries[i].Method, actualEntries[i].Method);
            }
        }

        [Theory]
        [InlineData("normal.zip")]
        [InlineData("small.zip")]
        public async Task ReadEntries_NonSeekableStream(string zipFile)
        {
            string path = zfile(zipFile);

            using var baseStream = await StreamHelpers.CreateTempCopyStream(path);
            using var nonSeekableStream = new NonSeekableStream(baseStream);
            using var reader = new ZipStreamReader(nonSeekableStream);

            int count = 0;
            while (reader.MoveToNextEntry())
            {
                var entry = reader.CurrentEntry!;
                Assert.NotNull(entry.FullName);
                Assert.True(entry.FullName.Length > 0 || entry.IsDirectory);

                using Stream entryStream = reader.OpenEntryStream();
                byte[] buffer = new byte[4096];
                while (entryStream.Read(buffer) > 0) { }

                count++;
            }

            Assert.True(count > 0, "Expected at least one entry");
        }

        [Theory]
        [InlineData("normal.zip")]
        [InlineData("small.zip")]
        public async Task ReadEntriesAsync_MatchesSync(string zipFile)
        {
            string path = zfile(zipFile);

            // Sync read
            var syncEntries = new List<string>();
            using (var stream = await StreamHelpers.CreateTempCopyStream(path))
            using (var reader = new ZipStreamReader(stream))
            {
                while (reader.MoveToNextEntry())
                {
                    syncEntries.Add(reader.CurrentEntry!.FullName);
                    using Stream entryStream = reader.OpenEntryStream();
                    byte[] buffer = new byte[4096];
                    while (entryStream.Read(buffer) > 0) { }
                }
            }

            // Async read
            var asyncEntries = new List<string>();
            using (var stream = await StreamHelpers.CreateTempCopyStream(path))
            using (var reader = new ZipStreamReader(stream))
            {
                while (await reader.MoveToNextEntryAsync())
                {
                    asyncEntries.Add(reader.CurrentEntry!.FullName);
                    using Stream entryStream = reader.OpenEntryStream();
                    byte[] buffer = new byte[4096];
                    while (await entryStream.ReadAsync(buffer) > 0) { }
                }
            }

            Assert.Equal(syncEntries, asyncEntries);
        }

        [Fact]
        public void ReadEntries_EmptyArchive()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                // Create empty archive
            }

            ms.Position = 0;
            using var reader = new ZipStreamReader(ms);
            Assert.False(reader.MoveToNextEntry());
            Assert.Null(reader.CurrentEntry);
        }

        [Fact]
        public void SkipEntry_WithoutOpening()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry1 = archive.CreateEntry("file1.txt");
                using (var writer = new StreamWriter(entry1.Open()))
                {
                    writer.Write("content1");
                }

                var entry2 = archive.CreateEntry("file2.txt");
                using (var writer = new StreamWriter(entry2.Open()))
                {
                    writer.Write("content2");
                }
            }

            ms.Position = 0;
            using var reader = new ZipStreamReader(ms);

            Assert.True(reader.MoveToNextEntry());
            Assert.Equal("file1.txt", reader.CurrentEntry!.FullName);

            // Skip first entry without reading
            Assert.True(reader.MoveToNextEntry());
            Assert.Equal("file2.txt", reader.CurrentEntry!.FullName);

            // Read second entry
            using Stream entryStream = reader.OpenEntryStream();
            using var sr = new StreamReader(entryStream);
            Assert.Equal("content2", sr.ReadToEnd());
        }

        [Fact]
        public void OpenEntryStream_Twice_Throws()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("test.txt");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("test");
                }
            }

            ms.Position = 0;
            using var reader = new ZipStreamReader(ms);

            reader.MoveToNextEntry();
            using Stream stream1 = reader.OpenEntryStream();

            Assert.Throws<InvalidOperationException>(() => reader.OpenEntryStream());
        }

        [Fact]
        public void OpenEntryStream_WithoutMoveToNext_Throws()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("test.txt");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("test");
                }
            }

            ms.Position = 0;
            using var reader = new ZipStreamReader(ms);

            Assert.Throws<InvalidOperationException>(() => reader.OpenEntryStream());
        }

        [Fact]
        public void Constructor_NullStream_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ZipStreamReader(null!));
        }

        [Fact]
        public void Constructor_NonReadableStream_Throws()
        {
            using var ms = new MemoryStream();
            ms.Close();
            Assert.Throws<ArgumentException>(() => new ZipStreamReader(ms));
        }

        [Fact]
        public void MoveToNextEntry_AfterDispose_Throws()
        {
            using var ms = new MemoryStream();
            var reader = new ZipStreamReader(ms);
            reader.Dispose();
            Assert.Throws<ObjectDisposedException>(() => reader.MoveToNextEntry());
        }

        [Fact]
        public void ValidateEntry_Crc32_AfterRead()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("test.txt");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("Hello, World!");
                }
            }

            ms.Position = 0;
            using var reader = new ZipStreamReader(ms);

            Assert.True(reader.MoveToNextEntry());
            var currentEntry = reader.CurrentEntry!;

            using (Stream entryStream = reader.OpenEntryStream())
            {
                byte[] buffer = new byte[4096];
                while (entryStream.Read(buffer) > 0) { }
            }

            Assert.True(currentEntry.ValidateEntry());
        }

        [Fact]
        public void ValidateEntry_BeforeRead_Throws()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("test.txt");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("test");
                }
            }

            ms.Position = 0;
            using var reader = new ZipStreamReader(ms);
            reader.MoveToNextEntry();

            Assert.Throws<InvalidOperationException>(() => reader.CurrentEntry!.ValidateEntry());
        }

        [Fact]
        public void EntryProperties_AreCorrect()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("subdir/test.txt", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("Hello, World!");
                }

                archive.CreateEntry("emptydir/");
            }

            ms.Position = 0;
            using var reader = new ZipStreamReader(ms);

            Assert.True(reader.MoveToNextEntry());
            var fileEntry = reader.CurrentEntry!;
            Assert.Equal("subdir/test.txt", fileEntry.FullName);
            Assert.Equal("test.txt", fileEntry.Name);
            Assert.False(fileEntry.IsDirectory);
            Assert.False(fileEntry.IsEncrypted);

            // Skip the entry data
            using (Stream s = reader.OpenEntryStream())
            {
                byte[] buf = new byte[4096];
                while (s.Read(buf) > 0) { }
            }

            Assert.True(reader.MoveToNextEntry());
            var dirEntry = reader.CurrentEntry!;
            Assert.Equal("emptydir/", dirEntry.FullName);
            Assert.True(dirEntry.IsDirectory);
        }

        [Fact]
        public void LeaveOpen_True_DoesNotCloseStream()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("test.txt");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("test");
                }
            }

            ms.Position = 0;
            using (var reader = new ZipStreamReader(ms, leaveOpen: true))
            {
                reader.MoveToNextEntry();
                using Stream s = reader.OpenEntryStream();
                byte[] buf = new byte[4096];
                while (s.Read(buf) > 0) { }
            }

            Assert.True(ms.CanRead, "Stream should still be open");
        }

        [Fact]
        public void LeaveOpen_False_ClosesStream()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("test.txt");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("test");
                }
            }

            ms.Position = 0;
            var reader = new ZipStreamReader(ms, leaveOpen: false);
            reader.MoveToNextEntry();
            using (Stream s = reader.OpenEntryStream())
            {
                byte[] buf = new byte[4096];
                while (s.Read(buf) > 0) { }
            }

            reader.Dispose();
            Assert.False(ms.CanRead, "Stream should be closed");
        }

        [Fact]
        public void MoveToNextEntry_AutoDrains_PartiallyReadStream()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry1 = archive.CreateEntry("file1.txt");
                using (var writer = new StreamWriter(entry1.Open()))
                {
                    writer.Write(new string('A', 10000));
                }

                var entry2 = archive.CreateEntry("file2.txt");
                using (var writer = new StreamWriter(entry2.Open()))
                {
                    writer.Write("content2");
                }
            }

            ms.Position = 0;
            using var reader = new ZipStreamReader(ms);

            Assert.True(reader.MoveToNextEntry());
            using (Stream entryStream = reader.OpenEntryStream())
            {
                // Read only 1 byte
                byte[] buf = new byte[1];
                entryStream.Read(buf);
            }

            // Should successfully advance to next entry
            Assert.True(reader.MoveToNextEntry());
            Assert.Equal("file2.txt", reader.CurrentEntry!.FullName);

            using Stream stream2 = reader.OpenEntryStream();
            using var sr = new StreamReader(stream2);
            Assert.Equal("content2", sr.ReadToEnd());
        }

        [Fact]
        public void StoredCompression_Works()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("stored.txt", CompressionLevel.NoCompression);
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("stored content");
                }
            }

            ms.Position = 0;
            using var reader = new ZipStreamReader(ms);

            Assert.True(reader.MoveToNextEntry());
            Assert.Equal(ZipCompressionMethod.Stored, reader.CurrentEntry!.CompressionMethod);

            using Stream entryStream = reader.OpenEntryStream();
            using var sr = new StreamReader(entryStream);
            Assert.Equal("stored content", sr.ReadToEnd());
        }

        [Theory]
        [InlineData("7zip.zip")]
        [InlineData("windows.zip")]
        [InlineData("sharpziplib.zip")]
        public async Task CompatibilityRead_FromDifferentTools(string zipFile)
        {
            string path = compat(zipFile);

            using var stream = await StreamHelpers.CreateTempCopyStream(path);
            using var reader = new ZipStreamReader(stream);

            int count = 0;
            while (reader.MoveToNextEntry())
            {
                var entry = reader.CurrentEntry!;
                Assert.NotNull(entry.FullName);

                using Stream entryStream = reader.OpenEntryStream();
                byte[] buffer = new byte[4096];
                while (entryStream.Read(buffer) > 0) { }

                count++;
            }

            Assert.True(count > 0);
        }

        [Fact]
        public void TolerantMode_InvalidData_ReturnsFalse()
        {
            byte[] garbage = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
            using var ms = new MemoryStream(garbage);
            using var reader = new ZipStreamReader(ms, entryNameEncoding: null, tolerant: true);

            Assert.False(reader.MoveToNextEntry());
        }

        [Fact]
        public void StrictMode_InvalidData_Throws()
        {
            byte[] garbage = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
            using var ms = new MemoryStream(garbage);
            using var reader = new ZipStreamReader(ms, entryNameEncoding: null, tolerant: false);

            Assert.Throws<InvalidDataException>(() => reader.MoveToNextEntry());
        }

        [Fact]
        public void ValidateEntry_WithErrorMessage()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("test.txt");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("test data");
                }
            }

            ms.Position = 0;
            using var reader = new ZipStreamReader(ms);

            reader.MoveToNextEntry();
            var currentEntry = reader.CurrentEntry!;

            using (Stream entryStream = reader.OpenEntryStream())
            {
                byte[] buffer = new byte[4096];
                while (entryStream.Read(buffer) > 0) { }
            }

            bool valid = currentEntry.ValidateEntry(out string? errorMessage);
            Assert.True(valid);
            Assert.Null(errorMessage);
        }

        [Fact]
        public async Task DisposeAsync_Works()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("test.txt");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("test");
                }
            }

            ms.Position = 0;
            var reader = new ZipStreamReader(ms, leaveOpen: false);
            await reader.MoveToNextEntryAsync();
            using (Stream s = reader.OpenEntryStream())
            {
                byte[] buf = new byte[4096];
                while (await s.ReadAsync(buf) > 0) { }
            }

            await reader.DisposeAsync();
            Assert.False(ms.CanRead);
        }

        [Fact]
        public void MultipleEntries_DeflateAndStored()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var deflateEntry = archive.CreateEntry("deflated.txt", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(deflateEntry.Open()))
                {
                    writer.Write("This is deflated content that should compress well. " + new string('X', 1000));
                }

                var storedEntry = archive.CreateEntry("stored.bin", CompressionLevel.NoCompression);
                using (var writer = new BinaryWriter(storedEntry.Open()))
                {
                    writer.Write(new byte[] { 1, 2, 3, 4, 5 });
                }
            }

            ms.Position = 0;
            using var reader = new ZipStreamReader(ms);

            Assert.True(reader.MoveToNextEntry());
            Assert.Equal("deflated.txt", reader.CurrentEntry!.FullName);
            using (Stream s = reader.OpenEntryStream())
            {
                byte[] buf = new byte[8192];
                while (s.Read(buf) > 0) { }
            }

            Assert.True(reader.MoveToNextEntry());
            Assert.Equal("stored.bin", reader.CurrentEntry!.FullName);
            Assert.Equal(ZipCompressionMethod.Stored, reader.CurrentEntry!.CompressionMethod);

            using (Stream s = reader.OpenEntryStream())
            {
                byte[] result = new byte[5];
                int totalRead = 0;
                while (totalRead < result.Length)
                {
                    int read = s.Read(result, totalRead, result.Length - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }

                Assert.Equal(5, totalRead);
                Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, result);
            }

            Assert.False(reader.MoveToNextEntry());
        }
    }
}
