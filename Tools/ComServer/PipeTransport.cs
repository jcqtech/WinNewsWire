using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinNewsWire.AppShared.Scripting;
using WinNewsWire.Core;

namespace WinNewsWire.Automation;

internal sealed class PipeTransport(string pipeName, Action launchApplication) : IAutomationTransport
{
    private readonly SemaphoreSlim _requests = new(1, 1);
    private bool _launchAttempted;

    public ScriptingResponse Send(ScriptingRequest request)
    {
        try { return SendAsync(request).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException
            or UnauthorizedAccessException or JsonException)
        {
            throw new COMException("WinNewsWire automation is unavailable. Start WinNewsWire and enable "
                + "'Allow local automation' in Preferences. " + error.Message, Errors.Unavailable);
        }
    }

    private async Task<ScriptingResponse> SendAsync(ScriptingRequest request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(300, deadline.Token).ConfigureAwait(false); }
            catch (TimeoutException) when (!_launchAttempted)
            {
                _launchAttempted = true;
                launchApplication();
                await pipe.ConnectAsync(15000, deadline.Token).ConfigureAwait(false);
            }
            await LocalAutomationPipe.WriteFrameAsync(pipe, JsonSerializer.Serialize(request, ScriptingProtocol.Options),
                LocalAutomationPipe.MaximumRequestBytes, deadline.Token).ConfigureAwait(false);
            var json = await LocalAutomationPipe.ReadFrameAsync(pipe, LocalAutomationPipe.MaximumResponseBytes,
                deadline.Token).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<ScriptingResponse>(json, ScriptingProtocol.Options)
                ?? throw new JsonException("The application returned an empty response.");
            if (response.ID != request.ID) throw new JsonException("The automation response ID did not match.");
            return response;
        }
        finally { _requests.Release(); }
    }

    internal static PipeTransport ForApplication(
#if PARITY_PROBE
        string? probeOutput = null
#endif
    ) => new(LocalAutomationPipe.NameForCurrentUser(AppConfig.DataDirectory), () =>
    {
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "WinNewsWire.exe"));
        if (!File.Exists(executable))
            throw new COMException("WinNewsWire.exe was not found beside the Automation directory. "
                + "Start WinNewsWire and enable local automation before retrying.", Errors.Unavailable);
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = true };
#if PARITY_PROBE
            if (probeOutput is not null)
            {
                start.ArgumentList.Add(probeOutput);
                start.ArgumentList.Add("--com-automation");
            }
#endif
            Process.Start(start)?.Dispose();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new COMException("WinNewsWire could not be started: " + error.Message, Errors.Unavailable);
        }
    });
}
