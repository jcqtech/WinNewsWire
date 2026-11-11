using System.Buffers;

namespace WinNewsWire.Web;

internal static class HttpContentReader
{
    internal const int MaximumFeedBytes = 64 * 1024 * 1024;
    // Async reads migrate between threads; use a bounded shared pool without per-thread retention.
    private static readonly ArrayPool<byte> Buffers = ArrayPool<byte>.Create(8 * 1024 * 1024, 2);

    internal static async Task<byte[]?> ReadAsync(HttpContent content, CancellationToken ct,
        Func<ReadOnlyMemory<byte>, bool>? shouldContinue = null)
    {
        if (content.Headers.ContentLength > MaximumFeedBytes)
            throw new InvalidDataException($"Response body exceeded the maximum allowed size of {MaximumFeedBytes} bytes.");
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = Buffers.Rent(8192);
        var chunk = Buffers.Rent(8192);
        int length = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, 8192), ct).ConfigureAwait(false);
                if (read == 0) break;
                if (length + read > MaximumFeedBytes)
                    throw new InvalidDataException($"Response body exceeded the maximum allowed size of {MaximumFeedBytes} bytes.");
                if (length + read > buffer.Length)
                {
                    var expanded = Buffers.Rent(Math.Min(MaximumFeedBytes, buffer.Length * 2));
                    buffer.AsSpan(0, length).CopyTo(expanded);
                    Buffers.Return(buffer);
                    buffer = expanded;
                }
                chunk.AsSpan(0, read).CopyTo(buffer.AsSpan(length));
                length += read;
                if (shouldContinue?.Invoke(new(buffer, 0, length)) == false) return null;
            }
            return buffer.AsSpan(0, length).ToArray();
        }
        finally
        {
            Buffers.Return(chunk);
            Buffers.Return(buffer);
        }
    }
}
