using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Xunit;

namespace WinNewsWire.Core.Tests;

public sealed class LocalAutomationPipeTests
{
    [Fact]
    public async Task FramesUseUtf8ByteLengthsAndRoundTripUnicode()
    {
        const string text = "{\"text\":\"caf\u00e9 \U0001f4f0\"}";
        using var stream = new MemoryStream();
        await LocalAutomationPipe.WriteFrameAsync(stream, text, 1000);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), BinaryPrimitives.ReadInt32LittleEndian(stream.ToArray()));
        stream.Position = 0;
        Assert.Equal(text, await LocalAutomationPipe.ReadFrameAsync(stream, 1000));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1025)]
    public async Task InvalidFrameLengthsAreRejectedBeforeAllocatingTheirBodies(int length)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalAutomationPipe.ReadFrameAsync(stream, 1024));
    }

    [Fact]
    public async Task TruncatedAndInvalidUtf8FramesFailExplicitly()
    {
        using var truncated = new MemoryStream(new byte[] { 3, 0, 0, 0, 65 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => LocalAutomationPipe.ReadFrameAsync(truncated, 1024));
        using var invalid = new MemoryStream(new byte[] { 2, 0, 0, 0, 0xc3, 0x28 });
        await Assert.ThrowsAsync<DecoderFallbackException>(() => LocalAutomationPipe.ReadFrameAsync(invalid, 1024));
        using var oversized = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalAutomationPipe.WriteFrameAsync(oversized, "abcde", 4));
        Assert.Equal(0, oversized.Length);
    }

    [Fact]
    public async Task CurrentUserPipeProcessesARequestAndRecoversFromAMalformedClient()
    {
        string name = "WinNewsWire-test-" + Guid.NewGuid().ToString("N");
        var errors = new ConcurrentQueue<Exception>();
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LocalAutomationPipe(name, (request, _) => Task.FromResult(request.ToUpperInvariant()),
            error => { errors.Enqueue(error); rejected.TrySetResult(); });
        await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using (var bad = Client(name))
        {
            await bad.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await bad.WriteAsync(new byte[4]);
            await bad.FlushAsync();
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using (var client = Client(name))
        {
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await LocalAutomationPipe.WriteFrameAsync(client, "hello", 100);
            Assert.Equal("HELLO", await LocalAutomationPipe.ReadFrameAsync(client, 100).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
    }

    [Fact]
    public async Task ShutdownCancelsAndDrainsAnOwnedRequest()
    {
        string name = "WinNewsWire-test-" + Guid.NewGuid().ToString("N");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        var server = new LocalAutomationPipe(name, async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cancelled.TrySetResult(); await release.Task; }
            return "{}";
        }, errors.Enqueue);
        try
        {
            await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var client = Client(name);
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await LocalAutomationPipe.WriteFrameAsync(client, "{}", 100);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = server.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
            release.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(errors);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => server.StartAsync());
        }
        finally { release.TrySetResult(); await server.DisposeAsync(); }
    }

    [Fact]
    public void PipeNamesAreUserAndProfileScopedWithoutExposingThoseValues()
    {
        var first = LocalAutomationPipe.NameForIdentity("user-one", @"D:\Profile");
        Assert.Equal(first, LocalAutomationPipe.NameForIdentity("user-one", @"d:\profile\"));
        Assert.NotEqual(first, LocalAutomationPipe.NameForIdentity("user-two", @"D:\Profile"));
        Assert.NotEqual(first, LocalAutomationPipe.NameForIdentity("user-one", @"D:\Other"));
        Assert.DoesNotContain("user-one", first);
        Assert.DoesNotContain("Profile", first);
    }

    private static NamedPipeClientStream Client(string name)
        => new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
