// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.Formats.Tar.Tests
{
    public partial class TarFile_ExtractToDirectoryAsync_File_Tests : TarFile_ExtractToDirectory_File_Tests_Base
    {
        protected override Task ExtractToDirectoryAsync(string sourceFileName, string destinationDirectoryName, bool overwriteFiles) =>
            TarFile.ExtractToDirectoryAsync(sourceFileName, destinationDirectoryName, overwriteFiles);

        protected override Task ExtractToDirectoryAsync(Stream source, string destinationDirectoryName, bool overwriteFiles) =>
            TarFile.ExtractToDirectoryAsync(source, destinationDirectoryName, overwriteFiles);

        protected override Task ExtractToDirectoryAsync(string sourceFileName, string destinationDirectoryName, TarExtractOptions options) =>
            TarFile.ExtractToDirectoryAsync(sourceFileName, destinationDirectoryName, options);

        [Fact]
        public Task ExtractToDirectoryAsync_Cancel()
        {
            CancellationTokenSource cs = new CancellationTokenSource();
            cs.Cancel();
            return Assert.ThrowsAsync<TaskCanceledException>(() => TarFile.ExtractToDirectoryAsync("file.tar", "directory", overwriteFiles: true, cs.Token));
        }
    }
}
