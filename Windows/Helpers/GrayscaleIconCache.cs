using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinNewsWire.Core;

namespace WinNewsWire.Helpers;

internal sealed class GrayscaleIconCache(DispatcherQueue dispatcher) : IAsyncDisposable
{
    private sealed class Entry(long revision)
    {
        internal long Revision { get; } = revision;
        internal BitmapImage Source { get; } = new();
        internal byte[]? Data;
        internal Task Loading = Task.CompletedTask;
    }
    private sealed record Luminance(ImageLuminanceType Type);
    private static readonly ConditionalWeakTable<ImageSource, Luminance> SourceLuminance = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _operations = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _shutdown;
    private bool _stopping;

    internal ImageSource SourceFor(string path, long revision) => EntryFor(path, revision).Source;

    internal async Task<byte[]> DataForAsync(string path, long revision, CancellationToken ct)
    {
        var entry = EntryFor(path, revision);
        await entry.Loading.WaitAsync(ct);
        return entry.Data ?? throw new InvalidDataException("The grayscale icon could not be loaded.");
    }

    private Entry EntryFor(string path, long revision)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_stopping, this);
        path = Path.GetFullPath(path);
        if (_entries.TryGetValue(path, out var cached) && cached.Revision == revision) return cached;
        var entry = new Entry(revision);
        _entries[path] = entry;
        entry.Loading = LoadAsync(path, entry, _lifetime.Token);
        _operations.Add(entry.Loading);
        _ = ObserveCompletionAsync(entry.Loading);
        return entry;
    }

    internal static bool TryGetLuminance(ImageSource source, out ImageLuminanceType type)
    {
        if (SourceLuminance.TryGetValue(source, out var value))
        {
            type = value.Type;
            return true;
        }
        type = default;
        return false;
    }

    private async Task LoadAsync(string path, Entry entry, CancellationToken ct)
    {
        try
        {
            var result = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                using var input = new MemoryStream();
                file.CopyTo(input);
                var data = WindowsImage.GrayscaleImageData(input.ToArray());
                return (Data: data, Luminance: WindowsImage.ClassifyLuminance(data));
            }, ct);
            ct.ThrowIfCancellationRequested();
            VerifyAccess();
            entry.Data = result.Data;
            SourceLuminance.Add(entry.Source, new(result.Luminance));
            using var stream = new MemoryStream(result.Data, writable: false);
            using var randomAccess = stream.AsRandomAccessStream();
            await entry.Source.SetSourceAsync(randomAccess);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException
            or ArgumentException or InvalidOperationException)
        {
            if (_entries.TryGetValue(path, out var current) && ReferenceEquals(current, entry)) _entries.Remove(path);
            App.LogException("Display grayscale feed icon", error);
        }
    }

    private async Task ObserveCompletionAsync(Task operation)
    {
        try { await operation; }
        finally { _operations.Remove(operation); }
    }

    internal void EmptyCache()
    {
        VerifyAccess();
        _entries.Clear();
    }

    public ValueTask DisposeAsync()
    {
        VerifyAccess();
        _stopping = true;
        return new(_shutdown ??= StopAsync());
    }

    private async Task StopAsync()
    {
        _lifetime.Cancel();
        await Task.WhenAll(_operations.ToArray());
        _entries.Clear();
        _lifetime.Dispose();
    }

    private void VerifyAccess()
    {
        if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("Grayscale icon sources require the UI dispatcher.");
    }
}
