using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace WinNewsWire.AppRuntime;

public sealed class ArticleSpeechService
{
    private CancellationTokenSource? _cancellation;
    private object? _owner;
    private Task _work = Task.CompletedTask;
    public bool IsSpeaking => _cancellation is not null;
    public event EventHandler? StateChanged;
#if PARITY_PROBE
    internal Func<string, CancellationToken, Task>? ProbePlayback { get; set; }
#endif

    public Task SpeakAsync(object owner, string text)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        CancelCurrent();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _owner = owner;
        _work = SpeakCoreAsync(_work, cancellation, text);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return _work;
    }

    public Task StopAsync(object? owner = null)
    {
        if (owner is not null && !ReferenceEquals(owner, _owner)) return Task.CompletedTask;
        CancelCurrent();
        return DrainAsync(_work);
    }

    private void CancelCurrent()
    {
        try { _cancellation?.Cancel(); }
        catch (AggregateException error) { App.LogException("Cancel article speech", error); }
    }

    private static async Task DrainAsync(Task work)
    {
        try { await work; }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException
            or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.IO.IOException)
        {
            App.LogException("Finish article speech", error);
        }
    }

    private async Task SpeakCoreAsync(Task previous, CancellationTokenSource cancellation, string text)
    {
        await Task.Yield();
        try
        {
            try { await previous; }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException
                or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.IO.IOException)
            {
                App.LogException("Previous speech session", error);
            }
            cancellation.Token.ThrowIfCancellationRequested();
#if PARITY_PROBE
            if (ProbePlayback is { } playback) await playback(text, cancellation.Token);
            else
#endif
                await PlaySpeechAsync(text, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
                _owner = null;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private static async Task PlaySpeechAsync(string text, CancellationToken ct)
    {
        using var synthesizer = new SpeechSynthesizer();
        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(ct);
        ct.ThrowIfCancellationRequested();
        using var source = MediaSource.CreateFromStream(stream, stream.ContentType);
        using var player = new MediaPlayer();
        player.CommandManager.IsEnabled = false;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Ended(MediaPlayer sender, object args) => completed.TrySetResult();
        void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
            => completed.TrySetException(new InvalidOperationException(args.ErrorMessage));
        player.MediaEnded += Ended;
        player.MediaFailed += Failed;
        try
        {
            player.Source = source;
            player.Play();
            await completed.Task.WaitAsync(ct);
        }
        finally
        {
            player.MediaEnded -= Ended;
            player.MediaFailed -= Failed;
            player.Pause();
            player.Source = null;
        }
    }
}
