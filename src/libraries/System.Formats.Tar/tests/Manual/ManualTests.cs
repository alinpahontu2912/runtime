// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace System.Formats.Tar.Tests;

public abstract class ManualTests_Base : TarTestsBase
{
    public static bool ManualTestsEnabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MANUAL_TESTS"));

    public static IEnumerable<object[]> WriteEntry_LongFileSize_TheoryData()
    {
        foreach (bool unseekableStream in new[] { false, true })
        {
            foreach (TarEntryFormat entryFormat in new[] { TarEntryFormat.V7, TarEntryFormat.Ustar, TarEntryFormat.Gnu, TarEntryFormat.Pax })
            {
                yield return new object[] { entryFormat, LegacyMaxFileSize, unseekableStream };
            }

            // Pax and Gnu supports unlimited size files.
            yield return new object[] { TarEntryFormat.Pax, LegacyMaxFileSize + 1, unseekableStream };
            yield return new object[] { TarEntryFormat.Gnu, LegacyMaxFileSize + 1, unseekableStream };
        }
    }

    protected abstract Task WriteEntryAsync(TarWriter writer, TarEntry entry);
    protected abstract Task<TarEntry?> GetNextEntryAsync(TarReader reader);
    protected abstract Task<int> ReadAsync(Stream stream, Memory<byte> buffer);
    protected abstract Task<int> ReadByteAsync(Stream stream);

    [ConditionalTheory(typeof(ManualTests), nameof(ManualTestsEnabled))]
    [MemberData(nameof(WriteEntry_LongFileSize_TheoryData))]
    [SkipOnPlatform(TestPlatforms.iOS | TestPlatforms.tvOS | TestPlatforms.Android | TestPlatforms.Browser, "Needs too much disk space.")]
    public async Task WriteEntry_LongFileSize(TarEntryFormat entryFormat, long size, bool unseekableStream)
    {
        // Write archive with a 8 Gb long entry.
        await using FileStream tarFile = File.Open(GetTestFilePath(), new FileStreamOptions { Access = FileAccess.ReadWrite, Mode = FileMode.Create, Options = FileOptions.DeleteOnClose });
        Stream s = unseekableStream ? new WrappedStream(tarFile, tarFile.CanRead, tarFile.CanWrite, canSeek: false) : tarFile;

        await using (TarWriter writer = new(s, leaveOpen: true))
        {
            TarEntry writeEntry = InvokeTarEntryCreationConstructor(entryFormat, GetRegularFileEntryTypeForFormat(entryFormat), "foo");
            writeEntry.DataStream = new SimulatedDataStream(size);
            await WriteEntryAsync(writer, writeEntry);
        }

        tarFile.Position = 0;

        // Read archive back.
        await using TarReader reader = new TarReader(s);
        TarEntry entry = await GetNextEntryAsync(reader);
        Assert.Equal(size, entry.Length);

        Stream dataStream = entry.DataStream;
        Assert.Equal(size, dataStream.Length);
        Assert.Equal(0, dataStream.Position);

        ReadOnlyMemory<byte> dummyData = SimulatedDataStream.DummyData;

        // Read the first bytes.
        Memory<byte> buffer = new byte[dummyData.Length];
        Assert.Equal(buffer.Length, await ReadAsync(dataStream, buffer));
        AssertExtensions.SequenceEqual(dummyData.Span, buffer.Span);
        Assert.Equal(0, await ReadByteAsync(dataStream)); // check next byte is correct.
        buffer.Span.Clear();

        // Read the last bytes.
        long dummyDataOffset = size - dummyData.Length - 1;
        if (dataStream.CanSeek)
        {
            Assert.False(unseekableStream);
            dataStream.Seek(dummyDataOffset, SeekOrigin.Begin);
        }
        else
        {
            Assert.True(unseekableStream);
            Memory<byte> seekBuffer = new byte[4_096];

            while (dataStream.Position < dummyDataOffset)
            {
                int bufSize = (int)Math.Min(seekBuffer.Length, dummyDataOffset - dataStream.Position);
                int res = await ReadAsync(dataStream, seekBuffer.Slice(0, bufSize));
                Assert.True(res > 0, "Unseekable stream finished before expected - Something went very wrong");
            }
        }

        Assert.Equal(0, await ReadByteAsync(dataStream)); // check previous byte is correct.
        Assert.Equal(buffer.Length, await ReadAsync(dataStream, buffer));
        AssertExtensions.SequenceEqual(dummyData.Span, buffer.Span);
        Assert.Equal(size, dataStream.Position);

        Assert.Null(await GetNextEntryAsync(reader));
    }
}

[OuterLoop]
[Collection(nameof(DisableParallelization))] // don't create multiple large files at the same time
public sealed class ManualTests : ManualTests_Base
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

    protected override Task<int> ReadAsync(Stream stream, Memory<byte> buffer)
    {
        try
        {
            return Task.FromResult(stream.Read(buffer.Span));
        }
        catch (Exception e)
        {
            return Task.FromException<int>(e);
        }
    }

    protected override Task<int> ReadByteAsync(Stream stream)
    {
        try
        {
            return Task.FromResult(stream.ReadByte());
        }
        catch (Exception e)
        {
            return Task.FromException<int>(e);
        }
    }
}

[OuterLoop]
[Collection(nameof(DisableParallelization))] // don't create multiple large files at the same time
public sealed class ManualTestsAsync : ManualTests_Base
{
    protected override Task WriteEntryAsync(TarWriter writer, TarEntry entry) =>
        writer.WriteEntryAsync(entry);

    protected override Task<TarEntry?> GetNextEntryAsync(TarReader reader) =>
        reader.GetNextEntryAsync().AsTask();

    protected override Task<int> ReadAsync(Stream stream, Memory<byte> buffer) =>
        stream.ReadAsync(buffer).AsTask();

    protected override async Task<int> ReadByteAsync(Stream stream)
    {
        byte[] buffer = new byte[1];
        int bytesRead = await stream.ReadAsync(buffer.AsMemory());
        return bytesRead == 0 ? -1 : buffer[0];
    }
}