// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace System.Formats.Tar.Tests
{
    // Shared test bodies for writing Gnu entries. Derived classes supply the sync or async
    // implementation of WriteEntryAsync so each test runs against both code paths.
    public abstract class TarWriter_WriteEntry_Gnu_Tests_Base : TarWriter_WriteEntry_Base
    {
        // Calls the synchronous WriteEntry or the asynchronous WriteEntryAsync depending on the derived class.
        protected abstract Task WriteEntryAsync(TarWriter writer, TarEntry entry);

        [Fact]
        public async Task WriteEntry_Null_Throws()
        {
            await using MemoryStream archiveStream = new MemoryStream();
            await using TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Gnu, leaveOpen: false);
            await Assert.ThrowsAsync<ArgumentNullException>(() => WriteEntryAsync(writer, null));
        }

        [Fact]
        public async Task WriteRegularFile()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Gnu, leaveOpen: true))
            {
                GnuTarEntry regularFile = new GnuTarEntry(TarEntryType.RegularFile, InitialEntryName);
                SetRegularFile(regularFile);
                VerifyRegularFile(regularFile, isWritable: true);
                await WriteEntryAsync(writer, regularFile);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                GnuTarEntry regularFile = reader.GetNextEntry() as GnuTarEntry;
                VerifyRegularFile(regularFile, isWritable: false);
            }
        }

        [Fact]
        public async Task WriteHardLink()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Gnu, leaveOpen: true))
            {
                GnuTarEntry hardLink = new GnuTarEntry(TarEntryType.HardLink, InitialEntryName);
                SetHardLink(hardLink);
                VerifyHardLink(hardLink);
                await WriteEntryAsync(writer, hardLink);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                GnuTarEntry hardLink = reader.GetNextEntry() as GnuTarEntry;
                VerifyHardLink(hardLink);
            }
        }

        [Fact]
        public async Task WriteSymbolicLink()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Gnu, leaveOpen: true))
            {
                GnuTarEntry symbolicLink = new GnuTarEntry(TarEntryType.SymbolicLink, InitialEntryName);
                SetSymbolicLink(symbolicLink);
                VerifySymbolicLink(symbolicLink);
                await WriteEntryAsync(writer, symbolicLink);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                GnuTarEntry symbolicLink = reader.GetNextEntry() as GnuTarEntry;
                VerifySymbolicLink(symbolicLink);
            }
        }

        [Fact]
        public async Task WriteDirectory()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Gnu, leaveOpen: true))
            {
                GnuTarEntry directory = new GnuTarEntry(TarEntryType.Directory, InitialEntryName);
                SetDirectory(directory);
                VerifyDirectory(directory);
                await WriteEntryAsync(writer, directory);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                GnuTarEntry directory = reader.GetNextEntry() as GnuTarEntry;
                VerifyDirectory(directory);
            }
        }

        [Fact]
        public async Task WriteCharacterDevice()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Gnu, leaveOpen: true))
            {
                GnuTarEntry charDevice = new GnuTarEntry(TarEntryType.CharacterDevice, InitialEntryName);
                SetCharacterDevice(charDevice);
                VerifyCharacterDevice(charDevice);
                await WriteEntryAsync(writer, charDevice);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                GnuTarEntry charDevice = reader.GetNextEntry() as GnuTarEntry;
                VerifyCharacterDevice(charDevice);
            }
        }

        [Fact]
        public async Task WriteBlockDevice()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Gnu, leaveOpen: true))
            {
                GnuTarEntry blockDevice = new GnuTarEntry(TarEntryType.BlockDevice, InitialEntryName);
                SetBlockDevice(blockDevice);
                VerifyBlockDevice(blockDevice);
                await WriteEntryAsync(writer, blockDevice);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                GnuTarEntry blockDevice = reader.GetNextEntry() as GnuTarEntry;
                VerifyBlockDevice(blockDevice);
            }
        }

        [Fact]
        public async Task WriteFifo()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Gnu, leaveOpen: true))
            {
                GnuTarEntry fifo = new GnuTarEntry(TarEntryType.Fifo, InitialEntryName);
                SetFifo(fifo);
                VerifyFifo(fifo);
                await WriteEntryAsync(writer, fifo);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                GnuTarEntry fifo = reader.GetNextEntry() as GnuTarEntry;
                VerifyFifo(fifo);
            }
        }

        [Theory]
        [InlineData(TarEntryType.RegularFile)]
        [InlineData(TarEntryType.Directory)]
        [InlineData(TarEntryType.SymbolicLink)]
        [InlineData(TarEntryType.HardLink)]
        public async Task Write_Long_Name(TarEntryType entryType)
        {
            // Name field in header only fits 100 bytes
            string longName = new string('a', 101);

            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Gnu, leaveOpen: true))
            {
                GnuTarEntry entry = new GnuTarEntry(entryType, longName);
                if (entryType is TarEntryType.HardLink or TarEntryType.SymbolicLink)
                {
                    entry.LinkName = "linktarget";
                }
                await WriteEntryAsync(writer, entry);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                GnuTarEntry entry = reader.GetNextEntry() as GnuTarEntry;
                Assert.Equal(entryType, entry.EntryType);
                Assert.Equal(longName, entry.Name);
            }
        }

        [Theory]
        [InlineData(TarEntryType.SymbolicLink)]
        [InlineData(TarEntryType.HardLink)]
        public async Task Write_LongLinkName(TarEntryType entryType)
        {
            // LinkName field in header only fits 100 bytes
            string longLinkName = new string('a', 101);

            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Gnu, leaveOpen: true))
            {
                GnuTarEntry entry = new GnuTarEntry(entryType, "file.txt");
                entry.LinkName = longLinkName;
                await WriteEntryAsync(writer, entry);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                GnuTarEntry entry = reader.GetNextEntry() as GnuTarEntry;
                Assert.Equal(entryType, entry.EntryType);
                Assert.Equal("file.txt", entry.Name);
                Assert.Equal(longLinkName, entry.LinkName);
            }
        }

        [Theory]
        [InlineData(TarEntryType.SymbolicLink)]
        [InlineData(TarEntryType.HardLink)]
        public async Task Write_LongName_And_LongLinkName(TarEntryType entryType)
        {
            // Both the Name and LinkName fields in header only fit 100 bytes
            string longName = new string('a', 101);
            string longLinkName = new string('a', 101);

            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Gnu, leaveOpen: true))
            {
                GnuTarEntry entry = new GnuTarEntry(entryType, longName);
                entry.LinkName = longLinkName;
                await WriteEntryAsync(writer, entry);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                GnuTarEntry entry = reader.GetNextEntry() as GnuTarEntry;
                Assert.Equal(entryType, entry.EntryType);
                Assert.Equal(longName, entry.Name);
                Assert.Equal(longLinkName, entry.LinkName);
            }
        }

        [Theory]
        [InlineData(TarEntryType.HardLink)]
        [InlineData(TarEntryType.SymbolicLink)]
        public async Task Write_LinkEntry_EmptyLinkName_Throws(TarEntryType entryType)
        {
            await using MemoryStream archiveStream = new MemoryStream();
            await using TarWriter writer = new TarWriter(archiveStream, leaveOpen: false);
            await Assert.ThrowsAsync<ArgumentException>("entry", () => WriteEntryAsync(writer, new GnuTarEntry(entryType, "link")));
        }
    }

    // Runs the shared Gnu write test bodies against the synchronous WriteEntry API.
    public sealed class TarWriter_WriteEntry_Gnu_Tests : TarWriter_WriteEntry_Gnu_Tests_Base
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

    // Runs the shared Gnu write test bodies against the asynchronous WriteEntryAsync API.
    public sealed class TarWriter_WriteEntryAsync_Gnu_Tests : TarWriter_WriteEntry_Gnu_Tests_Base
    {
        protected override Task WriteEntryAsync(TarWriter writer, TarEntry entry) =>
            writer.WriteEntryAsync(entry);
    }
}
