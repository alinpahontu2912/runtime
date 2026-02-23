// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO.Compression.Tests.Utilities;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace System.IO.Compression.Tests
{
    public class ZipInputStreamTests : ZipFileTestBase
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

            var expectedEntries = new List<(string Name, long Length, ZipCompressionMethod Method)>();
            using (var archiveStream = await StreamHelpers.CreateTempCopyStream(path))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    expectedEntries.Add((entry.FullName, entry.Length, entry.CompressionMethod));
                }
            }

            var actualEntries = new List<(string Name, long Length, ZipCompressionMethod Method)>();
            using (var stream = await StreamHelpers.CreateTempCopyStream(path))
            using (var zipStream = new ZipInputStream(stream))
            {
                while (zipStream.MoveToNextEntry())
                {
                    var entry = zipStream.CurrentEntry!;

                    long bytesRead = 0;
                    byte[] buffer = new byte[4096];
                    int read;
                    while ((read = zipStream.Read(buffer)) > 0)
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
            using var zipStream = new ZipInputStream(nonSeekableStream);

            int count = 0;
            while (zipStream.MoveToNextEntry())
            {
                var entry = zipStream.CurrentEntry!;
                Assert.NotNull(entry.FullName);
                Assert.True(entry.FullName.Length > 0 || entry.IsDirectory);

                byte[] buffer = new byte[4096];
                while (zipStream.Read(buffer) > 0) { }

                count++;
            }

            Assert.True(count > 0, "Expected at least one entry");
        }

        [Fact]
        public void ReadEntries_EmptyArchive()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
            }

            ms.Position = 0;
            using var zipStream = new ZipInputStream(ms);
            Assert.False(zipStream.MoveToNextEntry());
            Assert.Null(zipStream.CurrentEntry);
        }

        [Fact]
        public void SkipEntry_WithoutReading()
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
            using var zipStream = new ZipInputStream(ms);

            Assert.True(zipStream.MoveToNextEntry());
            Assert.Equal("file1.txt", zipStream.CurrentEntry!.FullName);

            // Skip first entry without reading
            Assert.True(zipStream.MoveToNextEntry());
            Assert.Equal("file2.txt", zipStream.CurrentEntry!.FullName);

            // Read second entry directly from the stream
            using var sr = new StreamReader(zipStream, leaveOpen: true);
            Assert.Equal("content2", sr.ReadToEnd());
        }

        [Fact]
        public void Read_WithoutMoveToNextEntry_ReturnsZero()
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
            using var zipStream = new ZipInputStream(ms);

            byte[] buffer = new byte[100];
            Assert.Equal(0, zipStream.Read(buffer));
        }

        [Fact]
        public void Constructor_NullStream_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ZipInputStream(null!));
        }

        [Fact]
        public void Constructor_NonReadableStream_Throws()
        {
            using var ms = new MemoryStream();
            ms.Close();
            Assert.Throws<ArgumentException>(() => new ZipInputStream(ms));
        }

        [Fact]
        public void MoveToNextEntry_AfterDispose_Throws()
        {
            using var ms = new MemoryStream();
            var zipStream = new ZipInputStream(ms);
            zipStream.Dispose();
            Assert.Throws<ObjectDisposedException>(() => zipStream.MoveToNextEntry());
        }

        [Fact]
        public void Read_AfterDispose_Throws()
        {
            using var ms = new MemoryStream();
            var zipStream = new ZipInputStream(ms);
            zipStream.Dispose();
            Assert.Throws<ObjectDisposedException>(() => zipStream.Read(new byte[10]));
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
            using var zipStream = new ZipInputStream(ms);

            Assert.True(zipStream.MoveToNextEntry());
            var fileEntry = zipStream.CurrentEntry!;
            Assert.Equal("subdir/test.txt", fileEntry.FullName);
            Assert.Equal("test.txt", fileEntry.Name);
            Assert.False(fileEntry.IsDirectory);
            Assert.False(fileEntry.IsEncrypted);

            byte[] buf = new byte[4096];
            while (zipStream.Read(buf) > 0) { }

            Assert.True(zipStream.MoveToNextEntry());
            var dirEntry = zipStream.CurrentEntry!;
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
            using (var zipStream = new ZipInputStream(ms, leaveOpen: true))
            {
                zipStream.MoveToNextEntry();
                byte[] buf = new byte[4096];
                while (zipStream.Read(buf) > 0) { }
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
            var zipStream = new ZipInputStream(ms, leaveOpen: false);
            zipStream.MoveToNextEntry();
            byte[] buf = new byte[4096];
            while (zipStream.Read(buf) > 0) { }

            zipStream.Dispose();
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
            using var zipStream = new ZipInputStream(ms);

            Assert.True(zipStream.MoveToNextEntry());
            // Read only 1 byte
            byte[] buf = new byte[1];
            zipStream.Read(buf);

            // Should successfully advance to next entry (auto-drains)
            Assert.True(zipStream.MoveToNextEntry());
            Assert.Equal("file2.txt", zipStream.CurrentEntry!.FullName);

            using var sr = new StreamReader(zipStream, leaveOpen: true);
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
            using var zipStream = new ZipInputStream(ms);

            Assert.True(zipStream.MoveToNextEntry());
            Assert.Equal(ZipCompressionMethod.Stored, zipStream.CurrentEntry!.CompressionMethod);

            using var sr = new StreamReader(zipStream, leaveOpen: true);
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
            using var zipStream = new ZipInputStream(stream);

            int count = 0;
            while (zipStream.MoveToNextEntry())
            {
                var entry = zipStream.CurrentEntry!;
                Assert.NotNull(entry.FullName);

                byte[] buffer = new byte[4096];
                while (zipStream.Read(buffer) > 0) { }

                count++;
            }

            Assert.True(count > 0);
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
            using var zipStream = new ZipInputStream(ms);

            Assert.True(zipStream.MoveToNextEntry());
            Assert.Equal("deflated.txt", zipStream.CurrentEntry!.FullName);
            byte[] buf = new byte[8192];
            while (zipStream.Read(buf) > 0) { }

            Assert.True(zipStream.MoveToNextEntry());
            Assert.Equal("stored.bin", zipStream.CurrentEntry!.FullName);
            Assert.Equal(ZipCompressionMethod.Stored, zipStream.CurrentEntry!.CompressionMethod);

            byte[] result = new byte[5];
            int totalRead = 0;
            while (totalRead < result.Length)
            {
                int read = zipStream.Read(result, totalRead, result.Length - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            Assert.Equal(5, totalRead);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, result);

            Assert.False(zipStream.MoveToNextEntry());
        }

        [Fact]
        public void StreamProperties_AreCorrect()
        {
            using var ms = new MemoryStream();
            using var zipStream = new ZipInputStream(ms);

            Assert.True(zipStream.CanRead);
            Assert.False(zipStream.CanSeek);
            Assert.False(zipStream.CanWrite);
            Assert.Throws<NotSupportedException>(() => zipStream.Length);
            Assert.Throws<NotSupportedException>(() => zipStream.Position);
            Assert.Throws<NotSupportedException>(() => zipStream.Position = 0);
            Assert.Throws<NotSupportedException>(() => zipStream.Seek(0, SeekOrigin.Begin));
            Assert.Throws<NotSupportedException>(() => zipStream.SetLength(0));
            Assert.Throws<NotSupportedException>(() => zipStream.Write(new byte[1], 0, 1));
        }

        [Fact]
        public void ReadByte_Works()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("test.txt", CompressionLevel.NoCompression);
                using (var s = entry.Open())
                {
                    s.WriteByte(0x42);
                }
            }

            ms.Position = 0;
            using var zipStream = new ZipInputStream(ms);

            zipStream.MoveToNextEntry();
            Assert.Equal(0x42, zipStream.ReadByte());
            Assert.Equal(-1, zipStream.ReadByte());
        }
    }
}
