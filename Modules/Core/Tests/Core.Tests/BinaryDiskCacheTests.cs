using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Core.Tests;

public sealed class BinaryDiskCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "binary-cache-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CallerSuppliedKeyIsStoredAsTheExactFilename()
    {
        const string key = "900150983cd24fb0d6963f7d28e17f72";
        var cache = new BinaryDiskCache(_directory);
        byte[] data = [0, 1, 255, 128];
        await cache.SetAsync(key, data);
        Assert.Equal(Path.Combine(_directory, key), cache.PathForKey(key));
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_directory, key)));
        Assert.Equal(data, await new BinaryDiskCache(_directory).GetAsync(key));
        Assert.Equal(key, Path.GetFileName(Assert.Single(Directory.GetFiles(_directory))));
    }

    [Fact]
    public async Task MissingReadsDoNotCreateDirectories()
    {
        var cache = new BinaryDiskCache(_directory);
        Assert.Null(await cache.GetAsync("missing"));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task EmptyValuesOverwriteAndDeleteRemainDistinctFromMissingValues()
    {
        var cache = new BinaryDiskCache(_directory);
        await cache.SetAsync("key", []);
        Assert.Empty(Assert.IsType<byte[]>(await cache.GetAsync("key")));
        await cache.SetAsync("key", [42]);
        Assert.Equal(new byte[] { 42 }, await cache.GetAsync("key"));
        cache.Remove("key");
        cache.Remove("key");
        Assert.Null(await cache.GetAsync("key"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingFromAMissingDirectoryIsIdempotent(bool previouslyWritten)
    {
        var cache = new BinaryDiskCache(_directory);
        if (previouslyWritten)
        {
            await cache.SetAsync("key", [1]);
            File.Delete(cache.PathForKey("key"));
            Directory.Delete(_directory);
        }

        cache.Remove("key");
        cache.Remove("key");
        Assert.False(Directory.Exists(_directory));
        Assert.Null(await cache.GetAsync("key"));
    }

    [Fact]
    public async Task FailedRemovalReportsTheIoErrorAndPreservesTheValue()
    {
        var cache = new BinaryDiskCache(_directory);
        await cache.SetAsync("key", [1]);
        using (var locked = new FileStream(cache.PathForKey("key"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => cache.Remove("key"));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(new byte[] { 1 }, await cache.GetAsync("key"));
        cache.Remove("key");
        Assert.Null(await cache.GetAsync("key"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("C:\\outside")]
    [InlineData("nested/file")]
    [InlineData("nested\\file")]
    [InlineData("file:stream")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("nul.bin")]
    [InlineData("LPT1.png")]
    [InlineData("COM\u00b9.bin")]
    [InlineData("CONOUT$")]
    [InlineData("control\0")]
    public async Task UnsafeWindowsFilenameKeysAreRejectedBeforeAnyIo(string key)
    {
        var cache = new BinaryDiskCache(_directory);
        Assert.Throws<ArgumentException>(() => cache.PathForKey(key));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.SetAsync(key, [1]));
        Assert.Throws<ArgumentException>(() => cache.Remove(key));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task SafeUnicodeFilenamesAreNotRehashedOrRenamed()
    {
        const string key = "caf\u00e9-cache.bin";
        var cache = new BinaryDiskCache(_directory);
        await cache.SetAsync(key, [7]);
        Assert.Equal(key, Path.GetFileName(Assert.Single(Directory.GetFiles(_directory))));
        Assert.Equal(new byte[] { 7 }, await cache.GetAsync(key));
    }

    [Fact]
    public async Task ConcurrentRequestsKeepAllPayloadsAndLeaveNoTemporaryFiles()
    {
        var cache = new BinaryDiskCache(_directory);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(async index =>
        {
            var data = Enumerable.Repeat((byte)index, 4096 + index).ToArray();
            await cache.SetAsync(index.ToString(), data);
            Assert.Equal(data, await cache.GetAsync(index.ToString()));
        }));
        Assert.Equal(32, Directory.GetFiles(_directory).Length);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task WritesRecreateAnExternallyPurgedCacheDirectory()
    {
        var cache = new BinaryDiskCache(_directory);
        await cache.SetAsync("key", [1]);
        Directory.Delete(_directory, recursive: true);
        await cache.SetAsync("key", [2]);
        Assert.Equal(new byte[] { 2 }, await cache.GetAsync("key"));
    }

    [Fact]
    public async Task CancellationDoesNotReplaceThePreviousValue()
    {
        var cache = new BinaryDiskCache(_directory);
        await cache.SetAsync("key", [1]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SetAsync("key", [2], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync("key", cancellation.Token));
        Assert.Equal(new byte[] { 1 }, await cache.GetAsync("key"));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task FailedReplacementPreservesTheOldFileAndCleansUpTemporaryFiles()
    {
        var cache = new BinaryDiskCache(_directory);
        await cache.SetAsync("key", [1]);
        using (var locked = new FileStream(cache.PathForKey("key"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => cache.GetAsync("key"));
            var error = await Record.ExceptionAsync(() => cache.SetAsync("key", [2]));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(new byte[] { 1 }, await cache.GetAsync("key"));
        Assert.Single(Directory.GetFiles(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
