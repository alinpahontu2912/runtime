// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;

namespace System.Formats.Tar.Tests
{
    // Runs the shared cross-platform file-write test bodies against the asynchronous WriteEntryAsync /
    // GetNextEntryAsync APIs. Platform-specific and RemoteExecutor-based tests live in the .Unix.cs / .Windows.cs partials.
    public partial class TarWriter_WriteEntryAsync_File_Tests : TarWriter_WriteEntry_File_Tests_Base
    {
        protected override Task WriteEntryAsync(TarWriter writer, string fileName, string entryName) =>
            writer.WriteEntryAsync(fileName, entryName);

        protected override async Task<TarEntry> GetNextEntryAsync(TarReader reader) =>
            await reader.GetNextEntryAsync();
    }
}