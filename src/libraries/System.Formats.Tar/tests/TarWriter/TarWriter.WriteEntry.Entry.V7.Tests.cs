// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace System.Formats.Tar.Tests
{
    // Shared test bodies for writing V7 entries. Derived classes supply the sync or async
    // implementation of WriteEntryAsync so each test runs against both code paths.
    public abstract class TarWriter_WriteEntry_V7_Tests_Base : TarWriter_WriteEntry_Base
    {
        // Calls the synchronous WriteEntry or the asynchronous WriteEntryAsync depending on the derived class.
        protected abstract Task WriteEntryAsync(TarWriter writer, TarEntry entry);

        [Fact]
        public async Task WriteEntry_Null_Throws()
        {
            await using MemoryStream archiveStream = new MemoryStream();
            await using TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.V7, leaveOpen: false);
            await Assert.ThrowsAsync<ArgumentNullException>(() => WriteEntryAsync(writer, null));
        }

        [Fact]
        public async Task WriteRegularFile()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.V7, leaveOpen: true))
            {
                V7TarEntry oldRegularFile = new V7TarEntry(TarEntryType.V7RegularFile, InitialEntryName);
                SetRegularFile(oldRegularFile);
                VerifyRegularFile(oldRegularFile, isWritable: true);
                await WriteEntryAsync(writer, oldRegularFile);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                V7TarEntry oldRegularFile = reader.GetNextEntry() as V7TarEntry;
                VerifyRegularFile(oldRegularFile, isWritable: false);
            }
        }

        [Fact]
        public async Task WriteHardLink()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.V7, leaveOpen: true))
            {
                V7TarEntry hardLink = new V7TarEntry(TarEntryType.HardLink, InitialEntryName);
                SetHardLink(hardLink);
                VerifyHardLink(hardLink);
                await WriteEntryAsync(writer, hardLink);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                V7TarEntry hardLink = reader.GetNextEntry() as V7TarEntry;
                VerifyHardLink(hardLink);
            }
        }

        [Fact]
        public async Task WriteSymbolicLink()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.V7, leaveOpen: true))
            {
                V7TarEntry symbolicLink = new V7TarEntry(TarEntryType.SymbolicLink, InitialEntryName);
                SetSymbolicLink(symbolicLink);
                VerifySymbolicLink(symbolicLink);
                await WriteEntryAsync(writer, symbolicLink);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                V7TarEntry symbolicLink = reader.GetNextEntry() as V7TarEntry;
                VerifySymbolicLink(symbolicLink);
            }
        }

        [Fact]
        public async Task WriteDirectory()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.V7, leaveOpen: true))
            {
                V7TarEntry directory = new V7TarEntry(TarEntryType.Directory, InitialEntryName);
                SetDirectory(directory);
                VerifyDirectory(directory);
                await WriteEntryAsync(writer, directory);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                V7TarEntry directory = reader.GetNextEntry() as V7TarEntry;
                VerifyDirectory(directory);
            }
        }

        [Theory]
        [InlineData(TarEntryType.HardLink)]
        [InlineData(TarEntryType.SymbolicLink)]
        public async Task Write_LinkEntry_EmptyLinkName_Throws(TarEntryType entryType)
        {
            await using MemoryStream archiveStream = new MemoryStream();
            await using TarWriter writer = new TarWriter(archiveStream, leaveOpen: false);
            await Assert.ThrowsAsync<ArgumentException>("entry", () => WriteEntryAsync(writer, new V7TarEntry(entryType, "link")));
        }
    }

    // Runs the shared V7 write test bodies against the synchronous WriteEntry API.
    public sealed class TarWriter_WriteEntry_V7_Tests : TarWriter_WriteEntry_V7_Tests_Base
    {
        protected override Task WriteEntryAsync(TarWriter writer, TarEntry entry)
        {
            try
            {
                writer.WriteEntry(entry);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }
    }

    // Runs the shared V7 write test bodies against the asynchronous WriteEntryAsync API.
    public sealed class TarWriter_WriteEntryAsync_V7_Tests : TarWriter_WriteEntry_V7_Tests_Base
    {
        protected override Task WriteEntryAsync(TarWriter writer, TarEntry entry) =>
            writer.WriteEntryAsync(entry);
    }
}
