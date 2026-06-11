// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.Formats.Tar.Tests
{
    public abstract class TarFile_CreateFromDirectory_File_Tests_Base : TarTestsBase
    {
        protected abstract Task CreateFromDirectoryAsync(string sourceDirectoryName, string destinationFileName, bool includeBaseDirectory);
        protected abstract Task CreateFromDirectoryAsync(string sourceDirectoryName, string destinationFileName, bool includeBaseDirectory, TarEntryFormat format);
        protected abstract Task CreateFromDirectoryAsync(string sourceDirectoryName, string destinationFileName, bool includeBaseDirectory, TarWriterOptions options);
        protected abstract Task<TarEntry?> GetNextEntryAsync(TarReader reader);

        [Fact]
        public async Task InvalidPaths_Throw()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => CreateFromDirectoryAsync(sourceDirectoryName: null, destinationFileName: "path", includeBaseDirectory: false));
            await Assert.ThrowsAsync<ArgumentException>(() => CreateFromDirectoryAsync(sourceDirectoryName: string.Empty, destinationFileName: "path", includeBaseDirectory: false));
            await Assert.ThrowsAsync<ArgumentNullException>(() => CreateFromDirectoryAsync(sourceDirectoryName: "path", destinationFileName: null, includeBaseDirectory: false));
            await Assert.ThrowsAsync<ArgumentException>(() => CreateFromDirectoryAsync(sourceDirectoryName: "path", destinationFileName: string.Empty, includeBaseDirectory: false));
        }

        [Fact]
        public async Task NonExistentDirectory_Throws()
        {
            using TempDirectory root = new TempDirectory();

            string dirPath = Path.Join(root.Path, "dir");
            string filePath = Path.Join(root.Path, "file.tar");

            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => CreateFromDirectoryAsync(sourceDirectoryName: "IDontExist", destinationFileName: filePath, includeBaseDirectory: false));
        }

        [Fact]
        public async Task DestinationExists_Throws()
        {
            using TempDirectory root = new TempDirectory();

            string dirPath = Path.Join(root.Path, "dir");
            Directory.CreateDirectory(dirPath);

            string filePath = Path.Join(root.Path, "file.tar");
            File.Create(filePath).Dispose();

            await Assert.ThrowsAsync<IOException>(() => CreateFromDirectoryAsync(sourceDirectoryName: dirPath, destinationFileName: filePath, includeBaseDirectory: false));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task VerifyIncludeBaseDirectory(bool includeBaseDirectory)
        {
            using TempDirectory source = new TempDirectory();
            using TempDirectory destination = new TempDirectory();

            UnixFileMode baseDirectoryMode = TestPermission1;
            SetUnixFileMode(source.Path, baseDirectoryMode);

            string fileName1 = "file1.txt";
            string filePath1 = Path.Join(source.Path, fileName1);
            File.Create(filePath1).Dispose();
            UnixFileMode filename1Mode = TestPermission2;
            SetUnixFileMode(filePath1, filename1Mode);

            string subDirectoryName = "dir/"; // The trailing separator is preserved in the TarEntry.Name
            string subDirectoryPath = Path.Join(source.Path, subDirectoryName);
            Directory.CreateDirectory(subDirectoryPath);
            UnixFileMode subDirectoryMode = TestPermission3;
            SetUnixFileMode(subDirectoryPath, subDirectoryMode);

            string fileName2 = "file2.txt";
            string filePath2 = Path.Join(subDirectoryPath, fileName2);
            File.Create(filePath2).Dispose();
            UnixFileMode filename2Mode = TestPermission4;
            SetUnixFileMode(filePath2, filename2Mode);

            string destinationArchiveFileName = Path.Join(destination.Path, "output.tar");
            await CreateFromDirectoryAsync(source.Path, destinationArchiveFileName, includeBaseDirectory);

            await using FileStream fileStream = File.OpenRead(destinationArchiveFileName);
            await using TarReader reader = new TarReader(fileStream);

            List<TarEntry> entries = new List<TarEntry>();

            TarEntry entry;
            while ((entry = await GetNextEntryAsync(reader)) != null)
            {
                entries.Add(entry);
            }

            int expectedCount = 3 + (includeBaseDirectory ? 1 : 0);
            Assert.Equal(expectedCount, entries.Count);

            string prefix = includeBaseDirectory ? Path.GetFileName(source.Path) + '/' : string.Empty;

            if (includeBaseDirectory)
            {
                TarEntry baseEntry = entries.FirstOrDefault(x =>
                    x.EntryType == TarEntryType.Directory &&
                    x.Name == prefix);
                Assert.NotNull(baseEntry);
                AssertEntryModeFromFileSystemEquals(baseEntry, baseDirectoryMode);
            }

            TarEntry entry1 = entries.FirstOrDefault(x =>
                x.EntryType == TarEntryType.RegularFile &&
                x.Name == prefix + fileName1);
            Assert.NotNull(entry1);
            AssertEntryModeFromFileSystemEquals(entry1, filename1Mode);

            TarEntry directory = entries.FirstOrDefault(x =>
                x.EntryType == TarEntryType.Directory &&
                x.Name == prefix + subDirectoryName);
            Assert.NotNull(directory);
            AssertEntryModeFromFileSystemEquals(directory, subDirectoryMode);

            string actualFileName2 = subDirectoryName + fileName2; // Notice the trailing separator in subDirectoryName
            TarEntry entry2 = entries.FirstOrDefault(x =>
                x.EntryType == TarEntryType.RegularFile &&
                x.Name == prefix + actualFileName2);
            Assert.NotNull(entry2);
            AssertEntryModeFromFileSystemEquals(entry2, filename2Mode);
        }

        [Fact]
        public async Task IncludeBaseDirectoryIfEmpty()
        {
            using TempDirectory source = new TempDirectory();
            using TempDirectory destination = new TempDirectory();

            string destinationArchiveFileName = Path.Join(destination.Path, "output.tar");
            await CreateFromDirectoryAsync(source.Path, destinationArchiveFileName, includeBaseDirectory: true);

            await using FileStream fileStream = File.OpenRead(destinationArchiveFileName);
            await using TarReader reader = new TarReader(fileStream);

            TarEntry entry = await GetNextEntryAsync(reader);
            Assert.NotNull(entry);
            Assert.Equal(TarEntryType.Directory, entry.EntryType);
            Assert.Equal(Path.GetFileName(source.Path) + '/', entry.Name);

            Assert.Null(await GetNextEntryAsync(reader));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task IncludeAllSegmentsOfPath(bool includeBaseDirectory)
        {
            using TempDirectory source = new TempDirectory();
            using TempDirectory destination = new TempDirectory();

            string segment1 = Path.Join(source.Path, "segment1");
            Directory.CreateDirectory(segment1);
            string segment2 = Path.Join(segment1, "segment2");
            Directory.CreateDirectory(segment2);
            string textFile = Path.Join(segment2, "file.txt");
            File.Create(textFile).Dispose();

            string destinationArchiveFileName = Path.Join(destination.Path, "output.tar");

            await CreateFromDirectoryAsync(source.Path, destinationArchiveFileName, includeBaseDirectory);

            await using FileStream fileStream = File.OpenRead(destinationArchiveFileName);
            await using TarReader reader = new TarReader(fileStream);

            string prefix = includeBaseDirectory ? Path.GetFileName(source.Path) + '/' : string.Empty;

            TarEntry entry;

            if (includeBaseDirectory)
            {
                entry = await GetNextEntryAsync(reader);
                Assert.NotNull(entry);
                Assert.Equal(TarEntryType.Directory, entry.EntryType);
                Assert.Equal(prefix, entry.Name);
            }

            entry = await GetNextEntryAsync(reader);
            Assert.NotNull(entry);
            Assert.Equal(TarEntryType.Directory, entry.EntryType);
            Assert.Equal(prefix + "segment1/", entry.Name);

            entry = await GetNextEntryAsync(reader);
            Assert.NotNull(entry);
            Assert.Equal(TarEntryType.Directory, entry.EntryType);
            Assert.Equal(prefix + "segment1/segment2/", entry.Name);

            entry = await GetNextEntryAsync(reader);
            Assert.NotNull(entry);
            Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
            Assert.Equal(prefix + "segment1/segment2/file.txt", entry.Name);

            Assert.Null(await GetNextEntryAsync(reader));
        }

        [ConditionalFact(typeof(MountHelper), nameof(MountHelper.CanCreateSymbolicLinks))]
        public async Task SkipRecursionIntoDirectorySymlinks()
        {
            using TempDirectory root = new TempDirectory();

            string destinationArchive = Path.Join(root.Path, "destination.tar");

            string externalDirectory = Path.Join(root.Path, "externalDirectory");
            Directory.CreateDirectory(externalDirectory);

            File.Create(Path.Join(externalDirectory, "file.txt")).Dispose();

            string sourceDirectoryName = Path.Join(root.Path, "baseDirectory");
            Directory.CreateDirectory(sourceDirectoryName);

            string subDirectory = Path.Join(sourceDirectoryName, "subDirectory");
            Directory.CreateSymbolicLink(subDirectory, externalDirectory); // Should not recurse here

            await CreateFromDirectoryAsync(sourceDirectoryName, destinationArchive, includeBaseDirectory: false);

            await using FileStream archiveStream = File.OpenRead(destinationArchive);
            await using TarReader reader = new(archiveStream, leaveOpen: false);

            TarEntry entry = await GetNextEntryAsync(reader);
            Assert.NotNull(entry);
            Assert.Equal("subDirectory", entry.Name);
            Assert.Equal(TarEntryType.SymbolicLink, entry.EntryType);

            Assert.Null(await GetNextEntryAsync(reader)); // file.txt should not be found
        }

        [ConditionalFact(typeof(MountHelper), nameof(MountHelper.CanCreateSymbolicLinks))]
        public async Task SkipRecursionIntoBaseDirectorySymlink()
        {
            using TempDirectory root = new TempDirectory();

            string destinationArchive = Path.Join(root.Path, "destination.tar");

            string externalDirectory = Path.Join(root.Path, "externalDirectory");
            Directory.CreateDirectory(externalDirectory);

            string subDirectory = Path.Join(externalDirectory, "subDirectory");
            Directory.CreateDirectory(subDirectory);

            string sourceDirectoryName = Path.Join(root.Path, "baseDirectory");
            Directory.CreateSymbolicLink(sourceDirectoryName, externalDirectory);

            await CreateFromDirectoryAsync(sourceDirectoryName, destinationArchive, includeBaseDirectory: true); // Base directory is a symlink, do not recurse

            await using FileStream archiveStream = File.OpenRead(destinationArchive);
            await using TarReader reader = new(archiveStream, leaveOpen: false);

            TarEntry entry = await GetNextEntryAsync(reader);
            Assert.NotNull(entry);
            Assert.Equal("baseDirectory/", entry.Name);
            Assert.Equal(TarEntryType.SymbolicLink, entry.EntryType);

            Assert.Null(await GetNextEntryAsync(reader));
        }

        [Theory]
        [MemberData(nameof(GetTarEntryFormats))]
        public async Task CreateFromDirectory_WithFormat(TarEntryFormat format)
        {
            using TempDirectory source = new TempDirectory();
            using TempDirectory destination = new TempDirectory();

            string fileName = "file.txt";
            File.Create(Path.Join(source.Path, fileName)).Dispose();

            string destinationArchiveFileName = Path.Join(destination.Path, "output.tar");
            await CreateFromDirectoryAsync(source.Path, destinationArchiveFileName, includeBaseDirectory: false, format);

            await using FileStream fileStream = File.OpenRead(destinationArchiveFileName);
            await using TarReader reader = new TarReader(fileStream);

            TarEntry entry = await GetNextEntryAsync(reader);
            Assert.NotNull(entry);
            Assert.Equal(format, entry.Format);
            Assert.Equal(fileName, entry.Name);

            Assert.Null(await GetNextEntryAsync(reader));
        }

        [Theory]
        [MemberData(nameof(GetInvalidTarEntryFormats))]
        public async Task CreateFromDirectory_InvalidFormat_Throws(TarEntryFormat format)
        {
            using TempDirectory source = new TempDirectory();
            using TempDirectory destination = new TempDirectory();
            string destinationArchiveFileName = Path.Join(destination.Path, "output.tar");

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>("format", () =>
                CreateFromDirectoryAsync(source.Path, destinationArchiveFileName, includeBaseDirectory: false, format));
        }

        [ConditionalTheory(typeof(MountHelper), nameof(MountHelper.CanCreateHardLinks))]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CreateFromDirectory_UsesWriterOptions(bool toggle)
        {
            // Toggle an option property to verify changing options changes the produced archive.
            bool preserveLinks = toggle;

            using TempDirectory source = CreateSourceDirectoryForCreateFromDirectory_UsesWriterOptions();
            using TempDirectory destination = new TempDirectory();

            TarWriterOptions options = new TarWriterOptions()
            {
                HardLinkMode = preserveLinks ? TarHardLinkMode.PreserveLink : TarHardLinkMode.CopyContents
            };

            string destinationArchiveFileName = Path.Join(destination.Path, "output.tar");
            await CreateFromDirectoryAsync(source.Path, destinationArchiveFileName, includeBaseDirectory: false, options);

            await using FileStream fileStream = File.OpenRead(destinationArchiveFileName);
            VerifyCreateFromDirectory_UsesWriterOptions(fileStream, preserveLinks);
        }
    }

    public sealed class TarFile_CreateFromDirectory_File_Tests : TarFile_CreateFromDirectory_File_Tests_Base
    {
        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, string destinationFileName, bool includeBaseDirectory)
        {
            try
            {
                TarFile.CreateFromDirectory(sourceDirectoryName, destinationFileName, includeBaseDirectory);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }

        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, string destinationFileName, bool includeBaseDirectory, TarEntryFormat format)
        {
            try
            {
                TarFile.CreateFromDirectory(sourceDirectoryName, destinationFileName, includeBaseDirectory, format);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }

        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, string destinationFileName, bool includeBaseDirectory, TarWriterOptions options)
        {
            try
            {
                TarFile.CreateFromDirectory(sourceDirectoryName, destinationFileName, includeBaseDirectory, options);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }

        protected override Task<TarEntry?> GetNextEntryAsync(TarReader reader)
        {
            try
            {
                return Task.FromResult(reader.GetNextEntry());
            }
            catch (Exception e)
            {
                return Task.FromException<TarEntry?>(e);
            }
        }
    }

    public sealed class TarFile_CreateFromDirectoryAsync_File_Tests : TarFile_CreateFromDirectory_File_Tests_Base
    {
        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, string destinationFileName, bool includeBaseDirectory) =>
            TarFile.CreateFromDirectoryAsync(sourceDirectoryName, destinationFileName, includeBaseDirectory);

        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, string destinationFileName, bool includeBaseDirectory, TarEntryFormat format) =>
            TarFile.CreateFromDirectoryAsync(sourceDirectoryName, destinationFileName, includeBaseDirectory, format);

        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, string destinationFileName, bool includeBaseDirectory, TarWriterOptions options) =>
            TarFile.CreateFromDirectoryAsync(sourceDirectoryName, destinationFileName, includeBaseDirectory, options);

        protected override Task<TarEntry?> GetNextEntryAsync(TarReader reader) =>
            reader.GetNextEntryAsync().AsTask();

        [Fact]
        public Task CreateFromDirectoryAsync_Cancel()
        {
            CancellationTokenSource cs = new CancellationTokenSource();
            cs.Cancel();
            return Assert.ThrowsAsync<TaskCanceledException>(() => TarFile.CreateFromDirectoryAsync("directory", "file.tar", includeBaseDirectory: false, cs.Token));
        }
    }
}
