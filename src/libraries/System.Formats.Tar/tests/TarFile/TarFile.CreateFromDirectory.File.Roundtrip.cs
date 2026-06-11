// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace System.Formats.Tar.Tests
{
    public abstract class TarFile_CreateFromDirectory_Roundtrip_Tests_Base : TarTestsBase
    {
        protected abstract Task CreateFromDirectoryAsync(string sourceDirectoryName, string destinationFileName, bool includeBaseDirectory);
        protected abstract Task ExtractToDirectoryAsync(Stream source, string destinationDirectoryName, bool overwriteFiles);

        [ConditionalTheory(typeof(MountHelper), nameof(MountHelper.CanCreateSymbolicLinks))]
        [InlineData("./file.txt", "subDirectory")]
        [InlineData("../file.txt", "subDirectory")]
        [InlineData("../file.txt", "subDirectory1/subDirectory1.1")]
        [InlineData("./file.txt", "subDirectory1/subDirectory1.1")]
        [InlineData("./file.txt", null)]
        public async Task SymlinkRelativeTargets_InsideTheArchive_RoundtripsSuccessfully(string symlinkTargetPath, string? subDirectory)
        {
            using TempDirectory root = new TempDirectory();

            string destinationArchive = Path.Join(root.Path, "destination.tar");

            string sourceDirectoryName = Path.Join(root.Path, "baseDirectory");
            Directory.CreateDirectory(sourceDirectoryName);

            string destinationDirectoryName = Path.Join(root.Path, "destinationDirectory");
            Directory.CreateDirectory(destinationDirectoryName);

            string sourceSubDirectory = Path.Join(sourceDirectoryName, subDirectory);
            if (subDirectory != null) Directory.CreateDirectory(sourceSubDirectory);

            File.Create(Path.Join(sourceDirectoryName, subDirectory, symlinkTargetPath)).Dispose();
            File.CreateSymbolicLink(Path.Join(sourceSubDirectory, "linkToFile"), symlinkTargetPath);

            await CreateFromDirectoryAsync(sourceDirectoryName, destinationArchive, includeBaseDirectory: false);

            await using FileStream archiveStream = File.OpenRead(destinationArchive);
            await ExtractToDirectoryAsync(archiveStream, destinationDirectoryName, overwriteFiles: true);

            string destinationSubDirectory = Path.Join(destinationDirectoryName, subDirectory);
            string symlinkPath = Path.Join(destinationSubDirectory, "linkToFile");
            Assert.True(File.Exists(symlinkPath));

            FileInfo? fileInfo = new(symlinkPath);
            Assert.Equal(symlinkTargetPath, fileInfo.LinkTarget);

            FileSystemInfo? symlinkTarget = File.ResolveLinkTarget(symlinkPath, returnFinalTarget: true);
            Assert.True(File.Exists(symlinkTarget.FullName));
        }

        [ConditionalTheory(typeof(MountHelper), nameof(MountHelper.CanCreateSymbolicLinks))]
        [InlineData("../file.txt", null)]
        [InlineData("../../file.txt", "subDirectory")]
        public async Task SymlinkRelativeTargets_OutsideTheArchive_Fails(string symlinkTargetPath, string? subDirectory)
        {
            using TempDirectory root = new TempDirectory();

            string destinationArchive = Path.Join(root.Path, "destination.tar");

            string sourceDirectoryName = Path.Join(root.Path, "baseDirectory");
            Directory.CreateDirectory(sourceDirectoryName);

            string destinationDirectoryName = Path.Join(root.Path, "destinationDirectory");
            Directory.CreateDirectory(destinationDirectoryName);

            string sourceSubDirectory = Path.Join(sourceDirectoryName, subDirectory);
            if (subDirectory != null) Directory.CreateDirectory(sourceSubDirectory);

            File.CreateSymbolicLink(Path.Join(sourceSubDirectory, "linkToFile"), symlinkTargetPath);

            await CreateFromDirectoryAsync(sourceDirectoryName, destinationArchive, includeBaseDirectory: false);

            await using FileStream archiveStream = File.OpenRead(destinationArchive);
            Exception exception = await Assert.ThrowsAsync<IOException>(() => ExtractToDirectoryAsync(archiveStream, destinationDirectoryName, overwriteFiles: true));

            Assert.Equal(SR.Format(SR.TarExtractingResultsLinkOutside, symlinkTargetPath, $"{destinationDirectoryName}{Path.DirectorySeparatorChar}"), exception.Message);
        }
    }

    public sealed class TarFile_CreateFromDirectory_Roundtrip_Tests : TarFile_CreateFromDirectory_Roundtrip_Tests_Base
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

    public sealed class TarFile_CreateFromDirectoryAsync_Roundtrip_Tests : TarFile_CreateFromDirectory_Roundtrip_Tests_Base
    {
        protected override Task CreateFromDirectoryAsync(string sourceDirectoryName, string destinationFileName, bool includeBaseDirectory) =>
            TarFile.CreateFromDirectoryAsync(sourceDirectoryName, destinationFileName, includeBaseDirectory);

        protected override Task ExtractToDirectoryAsync(Stream source, string destinationDirectoryName, bool overwriteFiles) =>
            TarFile.ExtractToDirectoryAsync(source, destinationDirectoryName, overwriteFiles);
    }
}
