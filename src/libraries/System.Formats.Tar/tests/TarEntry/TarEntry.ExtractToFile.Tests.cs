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
    public abstract class TarEntry_ExtractToFile_Tests_Base : TarTestsBase
    {
        protected abstract Task ExtractToFileAsync(TarEntry entry, string destinationFileName, bool overwrite);

        [Theory]
        [InlineData(TarEntryFormat.V7)]
        [InlineData(TarEntryFormat.Ustar)]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public async Task Constructor_Name_FullPath_DestinationDirectory_Mismatch_Throws(TarEntryFormat format)
        {
            using TempDirectory root = new TempDirectory();

            string fullPath = Path.Join(Path.GetPathRoot(root.Path), "dir", "file.txt");

            TarEntry entry = InvokeTarEntryCreationConstructor(format, GetTarEntryTypeForTarEntryFormat(TarEntryType.RegularFile, format), fullPath);

            entry.DataStream = new MemoryStream();
            entry.DataStream.Write(new byte[] { 0x1 });
            entry.DataStream.Seek(0, SeekOrigin.Begin);

            await Assert.ThrowsAsync<IOException>(() => ExtractToFileAsync(entry, root.Path, overwrite: false));

            Assert.False(File.Exists(fullPath));
        }

        [Theory]
        [InlineData(TarEntryFormat.V7)]
        [InlineData(TarEntryFormat.Ustar)]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public async Task Constructor_Name_FullPath_DestinationDirectory_Match_AdditionalSubdirectory_Throws(TarEntryFormat format)
        {
            using TempDirectory root = new TempDirectory();

            string fullPath = Path.Join(root.Path, "dir", "file.txt");

            TarEntry entry = InvokeTarEntryCreationConstructor(format, GetTarEntryTypeForTarEntryFormat(TarEntryType.RegularFile, format), fullPath);

            entry.DataStream = new MemoryStream();
            entry.DataStream.Write(new byte[] { 0x1 });
            entry.DataStream.Seek(0, SeekOrigin.Begin);

            await Assert.ThrowsAsync<IOException>(() => ExtractToFileAsync(entry, root.Path, overwrite: false));

            Assert.False(File.Exists(fullPath));
        }

        [Theory]
        [InlineData(TarEntryFormat.V7)]
        [InlineData(TarEntryFormat.Ustar)]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public async Task Constructor_Name_FullPath_DestinationDirectory_Match(TarEntryFormat format)
        {
            using TempDirectory root = new TempDirectory();

            string fullPath = Path.Join(root.Path, "file.txt");

            TarEntry entry = InvokeTarEntryCreationConstructor(format, GetTarEntryTypeForTarEntryFormat(TarEntryType.RegularFile, format), fullPath);

            entry.DataStream = new MemoryStream();
            entry.DataStream.Write(new byte[] { 0x1 });
            entry.DataStream.Seek(0, SeekOrigin.Begin);

            await ExtractToFileAsync(entry, fullPath, overwrite: false);

            Assert.True(File.Exists(fullPath));
        }

        [Theory]
        [MemberData(nameof(GetFormatsAndLinks))]
        public async Task ExtractToFile_Link_Throws(TarEntryFormat format, TarEntryType entryType)
        {
            using TempDirectory root = new TempDirectory();
            string fileName = "mylink";
            string fullPath = Path.Join(root.Path, fileName);

            string linkTarget = PlatformDetection.IsWindows ? @"C:\Windows\system32\notepad.exe" : "/usr/bin/nano";

            TarEntry entry = InvokeTarEntryCreationConstructor(format, entryType, fileName);
            entry.LinkName = linkTarget;

            await Assert.ThrowsAsync<InvalidOperationException>(() => ExtractToFileAsync(entry, fileName, overwrite: false));

            Assert.Equal(0, Directory.GetFileSystemEntries(root.Path).Count());
        }

        [Theory]
        [MemberData(nameof(GetFormatsAndFiles))]
        public async Task Extract(TarEntryFormat format, TarEntryType entryType)
        {
            using TempDirectory root = new TempDirectory();

            (string entryName, string destination, TarEntry entry) = Prepare_Extract(root, format, entryType);

            await ExtractToFileAsync(entry, destination, overwrite: true);

            Verify_Extract(destination, entry, entryType);
        }
    }

    public sealed class TarEntry_ExtractToFile_Tests : TarEntry_ExtractToFile_Tests_Base
    {
        protected override Task ExtractToFileAsync(TarEntry entry, string destinationFileName, bool overwrite)
        {
            try
            {
                entry.ExtractToFile(destinationFileName, overwrite);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }
    }

    public sealed class TarEntry_ExtractToFileAsync_Tests : TarEntry_ExtractToFile_Tests_Base
    {
        protected override Task ExtractToFileAsync(TarEntry entry, string destinationFileName, bool overwrite) =>
            entry.ExtractToFileAsync(destinationFileName, overwrite);

        [Theory]
        [InlineData(TarEntryFormat.V7)]
        [InlineData(TarEntryFormat.Ustar)]
        [InlineData(TarEntryFormat.Pax)]
        [InlineData(TarEntryFormat.Gnu)]
        public Task ExtractToFileAsync_Cancel(TarEntryFormat format)
        {
            TarEntry entry = InvokeTarEntryCreationConstructor(format, TarEntryType.Directory, "dir");
            CancellationTokenSource cs = new CancellationTokenSource();
            cs.Cancel();
            return Assert.ThrowsAsync<TaskCanceledException>(() => entry.ExtractToFileAsync("dir", overwrite: true, cs.Token));
        }
    }
}