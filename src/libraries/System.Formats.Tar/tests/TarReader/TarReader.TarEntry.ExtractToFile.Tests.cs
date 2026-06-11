// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace System.Formats.Tar.Tests
{
    public abstract partial class TarReader_TarEntry_ExtractToFile_Tests_Base : TarTestsBase
    {
        protected abstract Task<TarEntry?> GetNextEntryAsync(TarReader reader);
        protected abstract Task ExtractToFileAsync(TarEntry entry, string destinationFileName, bool overwrite);

        [Fact]
        public async Task ExtractEntriesWithSlashDotPrefix()
        {
            using TempDirectory root = new TempDirectory();

            await using MemoryStream archiveStream = GetStrangeTarMemoryStream("prefixDotSlashAndCurrentFolderEntry");
            await using (TarReader reader = new TarReader(archiveStream, leaveOpen: false))
            {
                string rootPath = Path.TrimEndingDirectorySeparator(root.Path);
                TarEntry entry;
                while ((entry = await GetNextEntryAsync(reader)) != null)
                {
                    Assert.NotNull(entry);
                    Assert.StartsWith("./", entry.Name);
                    // Normalize the path (remove redundant segments), remove trailing separators
                    // this is so the first entry can be skipped if it's the same as the root directory
                    string entryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Join(rootPath, entry.Name)));
                    if (entryPath != rootPath)
                    {
                        await ExtractToFileAsync(entry, entryPath, overwrite: true);
                        Assert.True(Path.Exists(entryPath), $"Entry was not extracted: {entryPath}");
                    }
                }
            }
        }
    }

    public sealed class TarReader_TarEntry_ExtractToFile_Tests : TarReader_TarEntry_ExtractToFile_Tests_Base
    {
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

    public sealed class TarReader_ExtractToFileAsync_Tests : TarReader_TarEntry_ExtractToFile_Tests_Base
    {
        protected override Task<TarEntry?> GetNextEntryAsync(TarReader reader) =>
            reader.GetNextEntryAsync().AsTask();

        protected override Task ExtractToFileAsync(TarEntry entry, string destinationFileName, bool overwrite) =>
            entry.ExtractToFileAsync(destinationFileName, overwrite);
    }
}