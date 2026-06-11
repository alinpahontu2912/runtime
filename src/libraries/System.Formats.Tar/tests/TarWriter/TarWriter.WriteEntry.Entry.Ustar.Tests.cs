// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace System.Formats.Tar.Tests
{
    // Shared test bodies for writing Ustar entries. Derived classes supply the sync or async
    // implementation of WriteEntryAsync so each test runs against both code paths.
    public abstract class TarWriter_WriteEntry_Ustar_Tests_Base : TarWriter_WriteEntry_Base
    {
        // Calls the synchronous WriteEntry or the asynchronous WriteEntryAsync depending on the derived class.
        protected abstract Task WriteEntryAsync(TarWriter writer, TarEntry entry);

        [Fact]
        public async Task WriteEntry_Null_Throws()
        {
            await using MemoryStream archiveStream = new MemoryStream();
            await using TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Ustar, leaveOpen: false);
            await Assert.ThrowsAsync<ArgumentNullException>(() => WriteEntryAsync(writer, null));
        }

        [Fact]
        public async Task WriteRegularFile()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Ustar, leaveOpen: true))
            {
                UstarTarEntry regularFile = new UstarTarEntry(TarEntryType.RegularFile, InitialEntryName);
                SetRegularFile(regularFile);
                VerifyRegularFile(regularFile, isWritable: true);
                await WriteEntryAsync(writer, regularFile);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                UstarTarEntry regularFile = reader.GetNextEntry() as UstarTarEntry;
                VerifyRegularFile(regularFile, isWritable: false);
            }
        }

        [Fact]
        public async Task WriteHardLink()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Ustar, leaveOpen: true))
            {
                UstarTarEntry hardLink = new UstarTarEntry(TarEntryType.HardLink, InitialEntryName);
                SetHardLink(hardLink);
                VerifyHardLink(hardLink);
                await WriteEntryAsync(writer, hardLink);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                UstarTarEntry hardLink = reader.GetNextEntry() as UstarTarEntry;
                VerifyHardLink(hardLink);
            }
        }

        [Fact]
        public async Task WriteSymbolicLink()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Ustar, leaveOpen: true))
            {
                UstarTarEntry symbolicLink = new UstarTarEntry(TarEntryType.SymbolicLink, InitialEntryName);
                SetSymbolicLink(symbolicLink);
                VerifySymbolicLink(symbolicLink);
                await WriteEntryAsync(writer, symbolicLink);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                UstarTarEntry symbolicLink = reader.GetNextEntry() as UstarTarEntry;
                VerifySymbolicLink(symbolicLink);
            }
        }

        [Fact]
        public async Task WriteDirectory()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Ustar, leaveOpen: true))
            {
                UstarTarEntry directory = new UstarTarEntry(TarEntryType.Directory, InitialEntryName);
                SetDirectory(directory);
                VerifyDirectory(directory);
                await WriteEntryAsync(writer, directory);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                UstarTarEntry directory = reader.GetNextEntry() as UstarTarEntry;
                VerifyDirectory(directory);
            }
        }

        [Fact]
        public async Task WriteCharacterDevice()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Ustar, leaveOpen: true))
            {
                UstarTarEntry charDevice = new UstarTarEntry(TarEntryType.CharacterDevice, InitialEntryName);
                SetCharacterDevice(charDevice);
                VerifyCharacterDevice(charDevice);
                await WriteEntryAsync(writer, charDevice);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                UstarTarEntry charDevice = reader.GetNextEntry() as UstarTarEntry;
                VerifyCharacterDevice(charDevice);
            }
        }

        [Fact]
        public async Task WriteBlockDevice()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Ustar, leaveOpen: true))
            {
                UstarTarEntry blockDevice = new UstarTarEntry(TarEntryType.BlockDevice, InitialEntryName);
                SetBlockDevice(blockDevice);
                VerifyBlockDevice(blockDevice);
                await WriteEntryAsync(writer, blockDevice);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                UstarTarEntry blockDevice = reader.GetNextEntry() as UstarTarEntry;
                VerifyBlockDevice(blockDevice);
            }
        }

        [Fact]
        public async Task WriteFifo()
        {
            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, TarEntryFormat.Ustar, leaveOpen: true))
            {
                UstarTarEntry fifo = new UstarTarEntry(TarEntryType.Fifo, InitialEntryName);
                SetFifo(fifo);
                VerifyFifo(fifo);
                await WriteEntryAsync(writer, fifo);
            }

            archiveStream.Position = 0;
            using (TarReader reader = new TarReader(archiveStream))
            {
                UstarTarEntry fifo = reader.GetNextEntry() as UstarTarEntry;
                VerifyFifo(fifo);
            }
        }

        [Theory]
        [InlineData(TarEntryType.HardLink)]
        [InlineData(TarEntryType.SymbolicLink)]
        public async Task Write_LinkEntry_EmptyLinkName_Throws(TarEntryType entryType)
        {
            await using MemoryStream archiveStream = new MemoryStream();
            await using TarWriter writer = new TarWriter(archiveStream, leaveOpen: false);
            await Assert.ThrowsAsync<ArgumentException>("entry", () => WriteEntryAsync(writer, new UstarTarEntry(entryType, "link")));
        }
    }

    // Runs the shared Ustar write test bodies against the synchronous WriteEntry API.
    public sealed class TarWriter_WriteEntry_Ustar_Tests : TarWriter_WriteEntry_Ustar_Tests_Base
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

    // Runs the shared Ustar write test bodies against the asynchronous WriteEntryAsync API.
    public sealed class TarWriter_WriteEntryAsync_Ustar_Tests : TarWriter_WriteEntry_Ustar_Tests_Base
    {
        protected override Task WriteEntryAsync(TarWriter writer, TarEntry entry) =>
            writer.WriteEntryAsync(entry);
    }
}
