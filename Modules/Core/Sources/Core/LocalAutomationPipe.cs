using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace WinNewsWire.Core;

public sealed partial class LocalAutomationPipe : IAsyncDisposable
{
    public const int MaximumRequestBytes = 1024 * 1024;
    public const int MaximumResponseBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _name;
    private readonly Func<string, CancellationToken, Task<string>> _handle;
    private readonly Action<Exception> _reportError;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _running;
    private Task? _disposing;
    private readonly object _gate = new();
    private bool _disposed;

    public LocalAutomationPipe(string name, Func<string, CancellationToken, Task<string>> handle, Action<Exception> reportError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(reportError);
        _name = name;
        _handle = handle;
        _reportError = reportError;
    }

    [SupportedOSPlatform("windows")]
    public static string NameForCurrentUser(string dataDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current Windows user could not be identified.");
        return NameForIdentity(sid, dataDirectory);
    }

    internal static string NameForIdentity(string identity, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var path = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(identity + "\n" + path)));
        return "WinNewsWire.Automation." + hash[..24];
    }

    public Task StartAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stop.IsCancellationRequested) throw new InvalidOperationException("Create a new automation endpoint after stopping it.");
            if (_running?.IsCompleted == true) throw new InvalidOperationException("The automation endpoint is no longer running.");
            if (_running is null) _running = RunAsync();
            return _ready.Task;
        }
    }

    public Task Completion { get { lock (_gate) return _running ?? Task.CompletedTask; } }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_disposing is not null) return _disposing;
            Cancel();
            return _running ?? Task.CompletedTask;
        }
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await using var pipe = OperatingSystem.IsWindows() ? CreateWindowsServer(_name)
                    : new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _ready.TrySetResult();
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    using var inputDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    inputDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                    string request = await ReadFrameAsync(pipe, MaximumRequestBytes, inputDeadline.Token).ConfigureAwait(false);
                    using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    operationDeadline.CancelAfter(TimeSpan.FromMinutes(5));
                    string response = await _handle(request, operationDeadline.Token).ConfigureAwait(false);
                    await WriteFrameAsync(pipe, response, MaximumResponseBytes, operationDeadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException
                    or DecoderFallbackException or EncoderFallbackException or ArgumentException)
                {
                    Report(error);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            _ready.TrySetException(error);
            Report(error);
        }
        finally
        {
            if (!_ready.Task.IsCompleted) _ready.TrySetCanceled();
        }
    }

    private void Report(Exception error)
    {
        try { _reportError(error); }
        catch (Exception reportingError) { Trace.TraceError($"Automation error reporting failed: {reportingError.GetType().Name}"); }
    }

    public static async Task<string> ReadFrameAsync(Stream stream, int maximumBytes, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException("The automation frame length is invalid or exceeds the limit.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        return Utf8.GetString(data);
    }

    public static async Task WriteFrameAsync(Stream stream, string value, int maximumBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        int length = Utf8.GetByteCount(value);
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException("The automation response exceeds its frame limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(Utf8.GetBytes(value), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private void Cancel()
    {
        try { _stop.Cancel(); }
        catch (AggregateException error) { Report(error); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            return new(_disposing ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        Cancel();
        try
        {
            if (_running is not null) await _running.ConfigureAwait(false);
        }
        finally { _stop.Dispose(); }
    }
}
