// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.Formats.Tar.Tests
{
    public abstract class TarFile_CreateFromDirectory_Stream_Tests_Base : TarTestsBase
    {
        protected abstract Task CreateFromDirectoryAsync(string sourceDirectoryName, Stream destination, bool includeBaseDirectory);
        protected abstract Task CreateFromDirectoryAsync(string sourceDirectoryName, Stream destination, bool includeBaseDirectory, TarEntryFormat format);
        protected abstract Task CreateFromDirectoryAsync(string sourceDirectoryName, Stream destination, bool includeBaseDirectory, TarWriterOptions options);
        protected abstract Task<TarEntry?> GetNextEntryAsync(TarReader reader);

        [Fact]
        public async Task InvalidPath_Throws()
        {
            await using MemoryStream archive = new MemoryStream();
            await Assert.ThrowsAsync<ArgumentNullException>(() => CreateFromDirectoryAsync(sourceDirectoryName: null, destination: archive, includeBaseDirectory: false));
            await Assert.ThrowsAsync<ArgumentException>(() => CreateFromDirectoryAsync(sourceDirectoryName: string.Empty, destination: archive, includeBaseDirectory: false));
        }

        [Fact]
        public async Task NullStream_Throws()
        {
            await using MemoryStream archive = new MemoryStream();
            await Assert.ThrowsAsync<ArgumentNullException>(() => CreateFromDirectoryAsync(sourceDirectoryName: "path", destination: null, includeBaseDirectory: false));
        }

        [Fact]
        public async Task UnwritableStream_Throws()
        {
            await using MemoryStream archive = new MemoryStream();
            await using WrappedStream unwritable = new WrappedStream(archive, canRead: true, canWrite: false, canSeek: true);
            await Assert.ThrowsAsync<ArgumentException>(() => CreateFromDirectoryAsync(sourceDirectoryName: "path", destination: unwritable, includeBaseDirectory: false));
        }

        [Fact]
        public async Task NonExistentDirectory_Throws()
        {
            using TempDirectory root = new TempDirectory();
            string dirPath = Path.Join(root.Path, "dir");

            await using MemoryStream archive = new MemoryStream();
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => CreateFromDirectoryAsync(sourceDirectoryName: dirPath, destination: archive, includeBaseDirectory: false));
        }

        [Theory]
        [MemberData(nameof(GetTarEntryFormats))]
        public async Task CreateFromDirectory_WithFormat(TarEntryFormat format)
        {
            using TempDirectory source = new TempDirectory();
            string fileName = "file.txt";
            File.Create(Path.Join(source.Path, fileName)).Dispose();

            await using MemoryStream archive = new MemoryStream();
            await CreateFromDirectoryAsync(source.Path, archive, includeBaseDirectory: false, format);

            archive.Position = 0;
            await using TarReader reader = new TarReader(archive);

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
            await using MemoryStream archive = new MemoryStream();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>("format", () =>
                CreateFromDirectoryAsync(source.Path, archive, includeBaseDirectory: false, format));
        }

        [ConditionalTheory(typeof(MountHelper), nameof(MountHelper.CanCreateHardLinks))]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CreateFromDirectory_UsesWriterOptions(bool toggle)
        {
            // Toggle an option property to verify changing options changes the produced archive.
            bool preserveLinks = toggle;

            using TempDirectory source = CreateSourceDirectoryForCreateFromDirectory_UsesWriterOptions();

            TarWriterOptions options = new TarWriterOptions()
            {
                HardLinkMode = preserveLinks ? TarHardLinkMode.PreserveLink : TarHardLinkMode.CopyContents
            };

            await using MemoryStream archive = new MemoryStream();
            await CreateFromDirectoryAsync(source.Path, archive, includeBaseDirectory: false, options);

            VerifyCreateFromDirectory_UsesWriterOptions(archive, preserveLinks);
        }
    }

    public sealed class TarFile_CreateFromDirectory_Stream_Tests : TarFile_CreateFromDirectory_Stream_Tests_Base
    {
        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, Stream destination, bool includeBaseDirectory)
        {
            try
            {
                TarFile.CreateFromDirectory(sourceDirectoryName, destination, includeBaseDirectory);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }

        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, Stream destination, bool includeBaseDirectory, TarEntryFormat format)
        {
            try
            {
                TarFile.CreateFromDirectory(sourceDirectoryName, destination, includeBaseDirectory, format);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }

        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, Stream destination, bool includeBaseDirectory, TarWriterOptions options)
        {
            try
            {
                TarFile.CreateFromDirectory(sourceDirectoryName, destination, includeBaseDirectory, options);
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

    public sealed class TarFile_CreateFromDirectoryAsync_Stream_Tests : TarFile_CreateFromDirectory_Stream_Tests_Base
    {
        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, Stream destination, bool includeBaseDirectory) =>
            TarFile.CreateFromDirectoryAsync(sourceDirectoryName, destination, includeBaseDirectory);

        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, Stream destination, bool includeBaseDirectory, TarEntryFormat format) =>
            TarFile.CreateFromDirectoryAsync(sourceDirectoryName, destination, includeBaseDirectory, format);

        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, Stream destination, bool includeBaseDirectory, TarWriterOptions options) =>
            TarFile.CreateFromDirectoryAsync(sourceDirectoryName, destination, includeBaseDirectory, options);

        protected override Task<TarEntry?> GetNextEntryAsync(TarReader reader) =>
            reader.GetNextEntryAsync().AsTask();

        [Fact]
        public async Task CreateFromDirectoryAsync_Cancel()
        {
            CancellationTokenSource cs = new CancellationTokenSource();
            cs.Cancel();

            await using MemoryStream archiveStream = new MemoryStream();
            await Assert.ThrowsAsync<TaskCanceledException>(() => TarFile.CreateFromDirectoryAsync("directory", archiveStream, includeBaseDirectory: false, cs.Token));
        }
    }
}
