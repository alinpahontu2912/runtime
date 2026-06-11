// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace System.Formats.Tar.Tests
{
    public abstract partial class TarReader_TarEntry_ExtractToFile_Tests_Base
    {
        [SkipOnPlatform(TestPlatforms.tvOS, "https://github.com/dotnet/runtime/issues/68360")]
        [SkipOnPlatform(TestPlatforms.LinuxBionic, "Not supported on Bionic")]
        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsNotPrivilegedProcess))]
        public async Task SpecialFile_Unelevated_Throws()
        {
            using TempDirectory root = new TempDirectory();
            await using MemoryStream ms = GetTarMemoryStream(CompressionMethod.Uncompressed, TestTarFormat.ustar, "specialfiles");

            await using (TarReader reader = new TarReader(ms))
            {
                string path = Path.Join(root.Path, "output");

                // Block device requires elevation for writing
                PosixTarEntry blockDevice = await GetNextEntryAsync(reader) as PosixTarEntry;
                Assert.NotNull(blockDevice);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ExtractToFileAsync(blockDevice, path, overwrite: false));
                Assert.False(File.Exists(path));

                // Character device requires elevation for writing
                PosixTarEntry characterDevice = await GetNextEntryAsync(reader) as PosixTarEntry;
                Assert.NotNull(characterDevice);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ExtractToFileAsync(characterDevice, path, overwrite: false));
                Assert.False(File.Exists(path));

                // Fifo does not require elevation, should succeed
                PosixTarEntry fifo = await GetNextEntryAsync(reader) as PosixTarEntry;
                Assert.NotNull(fifo);
                await ExtractToFileAsync(fifo, path, overwrite: false);
                Assert.True(File.Exists(path));

                Assert.Null(await GetNextEntryAsync(reader));
            }
        }
    }
}