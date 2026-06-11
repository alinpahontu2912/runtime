// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading.Tasks;

namespace System.Formats.Tar.Tests
{
    public partial class TarFile_ExtractToDirectory_File_Tests : TarFile_ExtractToDirectory_File_Tests_Base
    {
        protected override Task ExtractToDirectoryAsync(string sourceFileName, string destinationDirectoryName, bool overwriteFiles)
        {
            try
            {
                TarFile.ExtractToDirectory(sourceFileName, destinationDirectoryName, overwriteFiles);
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

        protected override Task ExtractToDirectoryAsync(string sourceFileName, string destinationDirectoryName, TarExtractOptions options)
        {
            try
            {
                TarFile.ExtractToDirectory(sourceFileName, destinationDirectoryName, options);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                return Task.FromException(e);
            }
        }
    }
}
