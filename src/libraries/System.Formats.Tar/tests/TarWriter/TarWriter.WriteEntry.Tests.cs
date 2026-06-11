// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.Formats.Tar.Tests
{
    // Shared format-independent TarWriter.WriteEntry test bodies. Derived classes supply the sync or
    // async implementations of the wrappers so each test runs against both code paths.
    public abstract class TarWriter_WriteEntry_Tests_Base : TarWriter_WriteEntry_Base
    {
        protected abstract Task WriteEntryAsync(TarWriter writer, TarEntry entry);
        protected abstract Task WriteEntryAsync(TarWriter writer, string fileName, string entryName);
        protected abstract Task<TarEntry> GetNextEntryAsync(TarReader reader);

        [Fact]
        public async Task WriteEntry_AfterDispose_Throws()
        {
            using MemoryStream archiveStream = new MemoryStream();
            TarWriter writer = new TarWriter(archiveStream);
            writer.Dispose();

            PaxTarEntry entry = new PaxTarEntry(TarEntryType.RegularFile, InitialEntryName);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => WriteEntryAsync(writer, entry));
        }

        [Theory]
        [InlineData(TarEntryFormat.V7)]
        [InlineData(TarEntryFormat.Ustar)]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public async Task WriteEntry_FromUnseekableStream_AdvanceDataStream_WriteFromThatPosition(TarEntryFormat format)
        {
            using MemoryStream source = GetTarMemoryStream(CompressionMethod.Uncompressed, TestTarFormat.ustar, "file");
            using WrappedStream unseekable = new WrappedStream(source, canRead: true, canWrite: true, canSeek: false);

            using MemoryStream destination = new MemoryStream();
            await using (TarReader reader1 = new TarReader(unseekable))
            {
                TarEntry entry = await GetNextEntryAsync(reader1);
                Assert.NotNull(entry);
                Assert.NotNull(entry.DataStream);
                entry.DataStream.ReadByte(); // Advance one byte, now the expected string would be "ello file"

                await using (TarWriter writer = new TarWriter(destination, TarEntryFormat.Ustar, leaveOpen: true))
                {
                    await WriteEntryAsync(writer, entry);
                    TarEntry dirEntry = InvokeTarEntryCreationConstructor(format, TarEntryType.Directory, "dir");
                    await WriteEntryAsync(writer, dirEntry); // To validate that next entry is not affected
                }
            }

            destination.Seek(0, SeekOrigin.Begin);
            await using (TarReader reader2 = new TarReader(destination))
            {
                TarEntry entry = await GetNextEntryAsync(reader2);
                Assert.NotNull(entry);
                Assert.NotNull(entry.DataStream);

                using (StreamReader streamReader = new StreamReader(entry.DataStream, leaveOpen: true))
                {
                    string contents = streamReader.ReadLine();
                    Assert.Equal("ello file", contents);
                }

                TarEntry dirEntry = await GetNextEntryAsync(reader2);
                Assert.NotNull(dirEntry);
                Assert.Equal(format, dirEntry.Format);
                Assert.Equal(TarEntryType.Directory, dirEntry.EntryType);
                Assert.Equal("dir", dirEntry.Name);

                Assert.Null(await GetNextEntryAsync(reader2));
            }
        }

        [Theory]
        [InlineData(TarEntryFormat.V7)]
        [InlineData(TarEntryFormat.Ustar)]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public async Task WriteEntry_RespectDefaultWriterFormat(TarEntryFormat expectedFormat)
        {
            using TempDirectory root = new TempDirectory();

            string path = Path.Join(root.Path, "file.txt");
            File.Create(path).Dispose();

            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, expectedFormat, leaveOpen: true))
            {
                await WriteEntryAsync(writer, path, "file.txt");
            }

            archiveStream.Position = 0;
            await using (TarReader reader = new TarReader(archiveStream, leaveOpen: false))
            {
                TarEntry entry = await GetNextEntryAsync(reader);
                Assert.Equal(expectedFormat, entry.Format);

                Type expectedType = GetTypeForFormat(expectedFormat);

                Assert.Equal(expectedType, entry.GetType());
            }
        }

        [Theory]
        [InlineData(TarEntryFormat.Ustar)]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public async Task Write_RegularFileEntry_In_V7Writer(TarEntryFormat entryFormat)
        {
            using MemoryStream archive = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archive, format: TarEntryFormat.V7, leaveOpen: true))
            {
                TarEntry entry = entryFormat switch
                {
                    TarEntryFormat.Ustar => new UstarTarEntry(TarEntryType.RegularFile, InitialEntryName),
                    TarEntryFormat.Pax => new PaxTarEntry(TarEntryType.RegularFile, InitialEntryName),
                    TarEntryFormat.Gnu => new GnuTarEntry(TarEntryType.RegularFile, InitialEntryName),
                    _ => throw new InvalidDataException($"Unexpected format: {entryFormat}")
                };

                // Should be written in the format of the entry
                await WriteEntryAsync(writer, entry);
            }

            archive.Seek(0, SeekOrigin.Begin);
            await using (TarReader reader = new TarReader(archive))
            {
                TarEntry entry = await GetNextEntryAsync(reader);
                Assert.NotNull(entry);
                Assert.Equal(entryFormat, entry.Format);

                switch (entryFormat)
                {
                    case TarEntryFormat.Ustar:
                        Assert.True(entry is UstarTarEntry);
                        break;
                    case TarEntryFormat.Pax:
                        Assert.True(entry is PaxTarEntry);
                        break;
                    case TarEntryFormat.Gnu:
                        Assert.True(entry is GnuTarEntry);
                        break;
                }

                Assert.Null(await GetNextEntryAsync(reader));
            }
        }

        [Theory]
        [InlineData(TarEntryFormat.Ustar)]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public async Task Write_V7RegularFileEntry_In_OtherFormatsWriter(TarEntryFormat writerFormat)
        {
            using MemoryStream archive = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archive, format: writerFormat, leaveOpen: true))
            {
                V7TarEntry entry = new V7TarEntry(TarEntryType.V7RegularFile, InitialEntryName);

                // Should be written in the format of the entry
                await WriteEntryAsync(writer, entry);
            }

            archive.Seek(0, SeekOrigin.Begin);
            await using (TarReader reader = new TarReader(archive))
            {
                TarEntry entry = await GetNextEntryAsync(reader);
                Assert.NotNull(entry);
                Assert.Equal(TarEntryFormat.V7, entry.Format);
                Assert.True(entry is V7TarEntry);

                Assert.Null(await GetNextEntryAsync(reader));
            }
        }

        [Theory]
        [InlineData(TarEntryFormat.V7)]
        [InlineData(TarEntryFormat.Ustar)]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public async Task ReadAndWriteMultipleGlobalExtendedAttributesEntries(TarEntryFormat format)
        {
            Dictionary<string, string> attrs = new Dictionary<string, string>()
            {
                { "hello", "world" },
                { "dotnet", "runtime" }
            };

            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, leaveOpen: true))
            {
                PaxGlobalExtendedAttributesTarEntry gea1 = new PaxGlobalExtendedAttributesTarEntry(attrs);
                await WriteEntryAsync(writer, gea1);

                TarEntry entry1 = InvokeTarEntryCreationConstructor(format, TarEntryType.Directory, "dir1");
                await WriteEntryAsync(writer, entry1);

                PaxGlobalExtendedAttributesTarEntry gea2 = new PaxGlobalExtendedAttributesTarEntry(attrs);
                await WriteEntryAsync(writer, gea2);

                TarEntry entry2 = InvokeTarEntryCreationConstructor(format, TarEntryType.Directory,  "dir2");
                await WriteEntryAsync(writer, entry2);
            }

            archiveStream.Position = 0;

            await using (TarReader reader = new TarReader(archiveStream, leaveOpen: false))
            {
                VerifyGlobalExtendedAttributesEntry(await GetNextEntryAsync(reader), attrs);
                VerifyDirectory(await GetNextEntryAsync(reader), format, "dir1");
                VerifyGlobalExtendedAttributesEntry(await GetNextEntryAsync(reader), attrs);
                VerifyDirectory(await GetNextEntryAsync(reader), format, "dir2");
                Assert.Null(await GetNextEntryAsync(reader));
            }
        }

        [Theory]
        [MemberData(nameof(WriteTimeStampsWithFormats_TheoryData))]
        public async Task WriteTimeStamps(TarEntryFormat format, DateTimeOffset timestamp)
        {
            TarEntry entry = InvokeTarEntryCreationConstructor(format, TarEntryType.Directory, "dir");

            entry.ModificationTime = timestamp;
            Assert.Equal(timestamp, entry.ModificationTime);

            if (entry is GnuTarEntry gnuEntry)
            {
                gnuEntry.AccessTime = timestamp;
                Assert.Equal(timestamp, gnuEntry.AccessTime);

                gnuEntry.ChangeTime = timestamp;
                Assert.Equal(timestamp, gnuEntry.ChangeTime);
            }

            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, leaveOpen: true))
            {
                await WriteEntryAsync(writer, entry);
            }

            archiveStream.Position = 0;
            await using (TarReader reader = new TarReader(archiveStream))
            {
                TarEntry readEntry = await GetNextEntryAsync(reader);
                Assert.NotNull(readEntry);

                Assert.Equal(timestamp, readEntry.ModificationTime);

                if (readEntry is GnuTarEntry gnuReadEntry)
                {
                    Assert.Equal(timestamp, gnuReadEntry.AccessTime);
                    Assert.Equal(timestamp, gnuReadEntry.ChangeTime);
                }
            }
        }

        [Theory]
        [MemberData(nameof(WriteIntField_TheoryData))]
        public async Task WriteUid(TarEntryFormat format, int value)
        {
            TarEntry entry = InvokeTarEntryCreationConstructor(format, TarEntryType.Directory, "dir");

            entry.Uid = value;
            Assert.Equal(value, entry.Uid);

            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, leaveOpen: true))
            {
                await WriteEntryAsync(writer, entry);
            }

            archiveStream.Position = 0;
            await using (TarReader reader = new TarReader(archiveStream))
            {
                TarEntry readEntry = await GetNextEntryAsync(reader);
                Assert.NotNull(readEntry);

                Assert.Equal(value, readEntry.Uid);
            }
        }

        [Theory]
        [MemberData(nameof(WriteIntField_TheoryData))]
        public async Task WriteGid(TarEntryFormat format, int value)
        {
            TarEntry entry = InvokeTarEntryCreationConstructor(format, TarEntryType.Directory, "dir");

            entry.Gid = value;
            Assert.Equal(value, entry.Gid);

            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, leaveOpen: true))
            {
                await WriteEntryAsync(writer, entry);
            }

            archiveStream.Position = 0;
            await using (TarReader reader = new TarReader(archiveStream))
            {
                TarEntry readEntry = await GetNextEntryAsync(reader);
                Assert.NotNull(readEntry);

                Assert.Equal(value, readEntry.Gid);
            }
        }

        [Theory]
        [MemberData(nameof(WriteIntField_TheoryData))]
        public async Task WriteDeviceMajor(TarEntryFormat format, int value)
        {
            if (format == TarEntryFormat.V7)
            {
                return; // No DeviceMajor
            }

            PosixTarEntry? entry = InvokeTarEntryCreationConstructor(format, TarEntryType.BlockDevice, "dir") as PosixTarEntry;
            Assert.NotNull(entry);

            entry.DeviceMajor = value;
            Assert.Equal(value, entry.DeviceMajor);

            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, leaveOpen: true))
            {
                await WriteEntryAsync(writer, entry);
            }

            archiveStream.Position = 0;
            await using (TarReader reader = new TarReader(archiveStream))
            {
                PosixTarEntry? readEntry = await GetNextEntryAsync(reader) as PosixTarEntry;
                Assert.NotNull(readEntry);

                Assert.Equal(value, readEntry.DeviceMajor);
            }
        }

        [Theory]
        [MemberData(nameof(WriteIntField_TheoryData))]
        public async Task WriteDeviceMinor(TarEntryFormat format, int value)
        {
            if (format == TarEntryFormat.V7)
            {
                return; // No DeviceMinor
            }

            PosixTarEntry? entry = InvokeTarEntryCreationConstructor(format, TarEntryType.BlockDevice, "dir") as PosixTarEntry;
            Assert.NotNull(entry);

            entry.DeviceMinor = value;
            Assert.Equal(value, entry.DeviceMinor);

            using MemoryStream archiveStream = new MemoryStream();
            await using (TarWriter writer = new TarWriter(archiveStream, leaveOpen: true))
            {
                await WriteEntryAsync(writer, entry);
            }

            archiveStream.Position = 0;
            await using (TarReader reader = new TarReader(archiveStream))
            {
                PosixTarEntry? readEntry = await GetNextEntryAsync(reader) as PosixTarEntry;
                Assert.NotNull(readEntry);

                Assert.Equal(value, readEntry.DeviceMinor);
            }
        }

        [Theory]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public async Task WriteLongName(TarEntryFormat format)
        {
            string maxPathComponent = new string('a', 255);
            await WriteLongNameCore(format, maxPathComponent);

            maxPathComponent = new string('a', 90) + new string('b', 165);
            await WriteLongNameCore(format, maxPathComponent);

            maxPathComponent = new string('a', 165) + new string('b', 90);
            await WriteLongNameCore(format, maxPathComponent);
        }

        private async Task WriteLongNameCore(TarEntryFormat format, string maxPathComponent)
        {
            Assert.Equal(255, maxPathComponent.Length);

            TarEntry entry;
            MemoryStream ms = new();
            await using (TarWriter writer = new(ms, true))
            {
                TarEntryType entryType = GetRegularFileEntryTypeForFormat(format);
                entry = InvokeTarEntryCreationConstructor(format, entryType, maxPathComponent);
                await WriteEntryAsync(writer, entry);

                entry = InvokeTarEntryCreationConstructor(format, entryType, Path.Join(maxPathComponent, maxPathComponent));
                await WriteEntryAsync(writer, entry);
            }

            ms.Position = 0;
            await using TarReader reader = new(ms);

            entry = await GetNextEntryAsync(reader);
            string expectedName = GetExpectedNameForFormat(format, maxPathComponent);
            Assert.Equal(expectedName, entry.Name);

            entry = await GetNextEntryAsync(reader);
            expectedName = GetExpectedNameForFormat(format, Path.Join(maxPathComponent, maxPathComponent));
            Assert.Equal(expectedName, entry.Name);

            Assert.Null(await GetNextEntryAsync(reader));

            string GetExpectedNameForFormat(TarEntryFormat format, string expectedName)
            {
                if (format is TarEntryFormat.V7) // V7 truncates names at 100 characters.
                {
                    return expectedName.Substring(0, 100);
                }
                return expectedName;
            }
        }

        public static IEnumerable<object[]> WriteEntry_TooLongName_Throws_TheoryData()
        {
            foreach (TarEntryType entryType in new[] { TarEntryType.RegularFile, TarEntryType.Directory })
            {
                foreach (string name in GetTooLongNamesTestData(NameCapabilities.Name))
                {
                    TarEntryType v7EntryType = entryType is TarEntryType.RegularFile ? TarEntryType.V7RegularFile : entryType;
                    yield return new object[] { TarEntryFormat.V7, v7EntryType, name };
                }

                foreach (string name in GetTooLongNamesTestData(NameCapabilities.NameAndPrefix))
                {
                    yield return new object[] { TarEntryFormat.Ustar, entryType, name };
                }
            }
        }

        [Theory]
        [MemberData(nameof(WriteEntry_TooLongName_Throws_TheoryData))]
        public async Task WriteEntry_TooLongName_Throws(TarEntryFormat entryFormat, TarEntryType entryType, string name)
        {
            await using TarWriter writer = new(new MemoryStream());

            TarEntry entry = InvokeTarEntryCreationConstructor(entryFormat, entryType, name);
            await Assert.ThrowsAsync<ArgumentException>("entry", () => WriteEntryAsync(writer, entry));
        }

        public static IEnumerable<object[]> WriteEntry_TooLongLinkName_Throws_TheoryData()
        {
            foreach (TarEntryType entryType in new[] { TarEntryType.SymbolicLink, TarEntryType.HardLink })
            {
                foreach (string name in GetTooLongNamesTestData(NameCapabilities.Name))
                {
                    yield return new object[] { TarEntryFormat.V7, entryType, name };
                }

                foreach (string name in GetTooLongNamesTestData(NameCapabilities.NameAndPrefix))
                {
                    yield return new object[] { TarEntryFormat.Ustar, entryType, name };
                }
            }
        }

        [Theory]
        [MemberData(nameof(WriteEntry_TooLongLinkName_Throws_TheoryData))]
        public async Task WriteEntry_TooLongLinkName_Throws(TarEntryFormat entryFormat, TarEntryType entryType, string linkName)
        {
            await using TarWriter writer = new(new MemoryStream());

            TarEntry entry = InvokeTarEntryCreationConstructor(entryFormat, entryType, "foo");
            entry.LinkName = linkName;

            await Assert.ThrowsAsync<ArgumentException>("entry", () => WriteEntryAsync(writer, entry));
        }

        public static IEnumerable<object[]> WriteEntry_TooLongUserGroupName_Throws_TheoryData()
        {
            // Not testing Pax as it supports unlimited size uname/gname.
            foreach (TarEntryFormat entryFormat in new[] { TarEntryFormat.Ustar, TarEntryFormat.Gnu })
            {
                // Last character doesn't fit fully.
                yield return new object[] { entryFormat, Repeat(OneByteCharacter, 32 + 1) };
                yield return new object[] { entryFormat, Repeat(TwoBytesCharacter, 32 / 2 + 1) };
                yield return new object[] { entryFormat, Repeat(FourBytesCharacter, 32 / 4 + 1) };

                // Last character doesn't fit by one byte.
                yield return new object[] { entryFormat, Repeat(TwoBytesCharacter, 32 - 2 + 1) + TwoBytesCharacter };
                yield return new object[] { entryFormat, Repeat(FourBytesCharacter, 32 - 4 + 1) + FourBytesCharacter };
            }
        }

        [Theory]
        [MemberData(nameof(WriteEntry_TooLongUserGroupName_Throws_TheoryData))]
        public async Task WriteEntry_TooLongUserName_Throws(TarEntryFormat entryFormat, string userName)
        {
            await using TarWriter writer = new(new MemoryStream());

            TarEntry entry = InvokeTarEntryCreationConstructor(entryFormat, TarEntryType.RegularFile, "foo");
            PosixTarEntry posixEntry = Assert.IsAssignableFrom<PosixTarEntry>(entry);
            posixEntry.UserName = userName;

            await Assert.ThrowsAsync<ArgumentException>("entry", () => WriteEntryAsync(writer, entry));
        }

        [Theory]
        [MemberData(nameof(WriteEntry_TooLongUserGroupName_Throws_TheoryData))]
        public async Task WriteEntry_TooLongGroupName_Throws(TarEntryFormat entryFormat, string groupName)
        {
            await using TarWriter writer = new(new MemoryStream());

            TarEntry entry = InvokeTarEntryCreationConstructor(entryFormat, TarEntryType.RegularFile, "foo");
            PosixTarEntry posixEntry = Assert.IsAssignableFrom<PosixTarEntry>(entry);
            posixEntry.GroupName = groupName;

            await Assert.ThrowsAsync<ArgumentException>("entry", () => WriteEntryAsync(writer, entry));
        }

        public static IEnumerable<object[]> WriteEntry_UsingTarEntry_FromTarReader_IntoTarWriter_TheoryData()
        {
            foreach (var entryFormat in new[] { TarEntryFormat.V7, TarEntryFormat.Ustar, TarEntryFormat.Pax, TarEntryFormat.Gnu })
            {
                foreach (var entryType in new[] { GetRegularFileEntryTypeForFormat(entryFormat), TarEntryType.Directory, TarEntryType.SymbolicLink })
                {
                    foreach (bool unseekableStream in new[] { false, true })
                    {
                        yield return new object[] { entryFormat, entryType, unseekableStream };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(WriteEntry_UsingTarEntry_FromTarReader_IntoTarWriter_TheoryData))]
        public async Task WriteEntry_UsingTarEntry_FromTarReader_IntoTarWriter(TarEntryFormat entryFormat, TarEntryType entryType, bool unseekableStream)
        {
            MemoryStream msSource = new();
            MemoryStream msDestination = new();

            WriteTarArchiveWithOneEntry(msSource, entryFormat, entryType);
            msSource.Position = 0;

            Stream source = new WrappedStream(msSource, msSource.CanRead, msSource.CanWrite, canSeek: !unseekableStream);
            Stream destination = new WrappedStream(msDestination, msDestination.CanRead, msDestination.CanWrite, canSeek: !unseekableStream);

            await using (TarReader reader = new(source))
            await using (TarWriter writer = new(destination))
            {
                TarEntry entry;
                while ((entry = await GetNextEntryAsync(reader)) != null)
                {
                    await WriteEntryAsync(writer, entry);
                }
            }

            AssertExtensions.SequenceEqual(msSource.ToArray(), msDestination.ToArray());
        }

        [Theory]
        [InlineData(TarEntryFormat.V7)]
        [InlineData(TarEntryFormat.Ustar)]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public async Task WritingUnseekableDataStream_To_UnseekableArchiveStream_Throws(TarEntryFormat entryFormat)
        {
            using MemoryStream internalDataStream = new();
            using WrappedStream unseekableDataStream = new(internalDataStream, canRead: true, canWrite: false, canSeek: false);

            using MemoryStream internalArchiveStream = new();
            using WrappedStream unseekableArchiveStream = new(internalArchiveStream, canRead: true, canWrite: true, canSeek: false);

            await using TarWriter writer = new(unseekableArchiveStream);
            TarEntry entry = InvokeTarEntryCreationConstructor(entryFormat, GetTarEntryTypeForTarEntryFormat(TarEntryType.RegularFile, entryFormat), "file.txt");
            entry.DataStream = unseekableDataStream;
            await Assert.ThrowsAsync<IOException>(() => WriteEntryAsync(writer, entry));
        }

        [Theory]
        [InlineData(TarEntryFormat.V7)]
        [InlineData(TarEntryFormat.Ustar)]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public async Task Write_TwoEntries_With_UnseekableDataStreams(TarEntryFormat entryFormat)
        {
            byte[] expectedBytes = new byte[] { 0x1, 0x2, 0x3, 0x4, 0x5 };

            using MemoryStream internalDataStream1 = new();
            internalDataStream1.Write(expectedBytes.AsSpan());
            internalDataStream1.Position = 0;

            TarEntryType fileEntryType = GetTarEntryTypeForTarEntryFormat(TarEntryType.RegularFile, entryFormat);

            using WrappedStream unseekableDataStream1 = new(internalDataStream1, canRead: true, canWrite: false, canSeek: false);
            TarEntry entry1 = InvokeTarEntryCreationConstructor(entryFormat, fileEntryType, "file1.txt");
            entry1.DataStream = unseekableDataStream1;

            using MemoryStream internalDataStream2 = new();
            internalDataStream2.Write(expectedBytes.AsSpan());
            internalDataStream2.Position = 0;

            using WrappedStream unseekableDataStream2 = new(internalDataStream2, canRead: true, canWrite: false, canSeek: false);
            TarEntry entry2 = InvokeTarEntryCreationConstructor(entryFormat, fileEntryType, "file2.txt");
            entry2.DataStream = unseekableDataStream2;

            using MemoryStream archiveStream = new();
            await using (TarWriter writer = new(archiveStream, leaveOpen: true))
            {
                await WriteEntryAsync(writer, entry1); // Should not throw
                await WriteEntryAsync(writer, entry2); // To verify that second entry is written in correct place
            }

            // Verify
            archiveStream.Position = 0;
            byte[] actualBytes = new byte[] { 0, 0, 0, 0, 0 };
            await using (TarReader reader = new(archiveStream))
            {
                TarEntry readEntry = await GetNextEntryAsync(reader);
                Assert.NotNull(readEntry);
                readEntry.DataStream.ReadExactly(actualBytes);
                Assert.Equal(expectedBytes, actualBytes);

                readEntry = await GetNextEntryAsync(reader);
                Assert.NotNull(readEntry);
                readEntry.DataStream.ReadExactly(actualBytes);
                Assert.Equal(expectedBytes, actualBytes);

                Assert.Null(await GetNextEntryAsync(reader));
            }
        }
    }

    // Runs the shared format-independent bodies against the synchronous WriteEntry / GetNextEntry APIs.
    public sealed class TarWriter_WriteEntry_Tests : TarWriter_WriteEntry_Tests_Base
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

        protected override Task WriteEntryAsync(TarWriter writer, string fileName, string entryName)
        {
            try
            {
                writer.WriteEntry(fileName, entryName);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }

        protected override Task<TarEntry> GetNextEntryAsync(TarReader reader)
        {
            try
            {
                return Task.FromResult(reader.GetNextEntry());
            }
            catch (Exception e)
            {
                return Task.FromException<TarEntry>(e);
            }
        }
    }

    // Runs the shared format-independent bodies against the asynchronous WriteEntryAsync / GetNextEntryAsync
    // APIs, plus async-only tests that have no synchronous counterpart.
    public sealed class TarWriter_WriteEntryAsync_Tests : TarWriter_WriteEntry_Tests_Base
    {
        protected override Task WriteEntryAsync(TarWriter writer, TarEntry entry) =>
            writer.WriteEntryAsync(entry);

        protected override Task WriteEntryAsync(TarWriter writer, string fileName, string entryName) =>
            writer.WriteEntryAsync(fileName, entryName);

        protected override async Task<TarEntry> GetNextEntryAsync(TarReader reader) =>
            await reader.GetNextEntryAsync();

        [Theory]
        [InlineData(TarEntryFormat.V7)]
        [InlineData(TarEntryFormat.Ustar)]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public async Task WriteEntryAsync_Cancel(TarEntryFormat format)
        {
            CancellationTokenSource cs = new CancellationTokenSource();
            cs.Cancel();
            await using (MemoryStream archiveStream = new MemoryStream())
            {
                await using (TarWriter writer = new TarWriter(archiveStream, leaveOpen: false))
                {
                    TarEntry entry = InvokeTarEntryCreationConstructor(format, TarEntryType.Directory, "dir");
                    await Assert.ThrowsAsync<TaskCanceledException>(() => writer.WriteEntryAsync(entry, cs.Token));
                    await Assert.ThrowsAsync<TaskCanceledException>(() => writer.WriteEntryAsync("file.txt", "file.txt", cs.Token));
                }
            }
        }
    }
}
