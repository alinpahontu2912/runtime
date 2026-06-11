// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.Formats.Tar.Tests
{
    public sealed class TarFile_ExtractToDirectory_Stream_Tests : TarFile_ExtractToDirectory_Tests
    {
        protected override Task ExtractToDirectoryAsync(Stream source, string destinationDirectoryName, bool overwriteFiles)
        {
            try
            {
                TarFile.ExtractToDirectory(source, destinationDirectoryName, overwriteFiles);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }
    }

    public sealed class TarFile_ExtractToDirectoryAsync_Stream_Tests : TarFile_ExtractToDirectory_Tests
    {
        protected override Task ExtractToDirectoryAsync(Stream source, string destinationDirectoryName, bool overwriteFiles) =>
            TarFile.ExtractToDirectoryAsync(source, destinationDirectoryName, overwriteFiles);

        [Fact]
        public async Task ExtractToDirectoryAsync_Cancel()
        {
            CancellationTokenSource cs = new CancellationTokenSource();
            cs.Cancel();
            using (MemoryStream archiveStream = new MemoryStream())
            {
                await Assert.ThrowsAsync<TaskCanceledException>(() => TarFile.ExtractToDirectoryAsync(archiveStream, "directory", overwriteFiles: true, cs.Token));
            }
        }
    }
}
