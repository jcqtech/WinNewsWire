using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinNewsWire.AppShared.Scripting;
using WinNewsWire.AppShared;
using WinNewsWire.Core;

namespace WinNewsWire.AppRuntime;

public sealed partial class AppService
{
    private LocalAutomationPipe? _automation;
    private Task _automationChanges = Task.CompletedTask;
    private int _automationGeneration;
    public string AutomationStatus { get; private set; } = AppStrings.Get("AutomationDisabled");
    public event EventHandler? AutomationStateChanged;

    private void QueueAutomationUpdate()
    {
        if (_lifetime.IsCancellationRequested) return;
        _automationChanges = UpdateAutomationAsync(_automationChanges, ++_automationGeneration);
    }

    private async Task UpdateAutomationAsync(Task previous, int generation)
    {
        await Task.Yield();
        await previous;
        if (generation != _automationGeneration) return;
        try
        {
            if (_automation is not null)
            {
                var previousEndpoint = _automation;
                _automation = null;
                await previousEndpoint.DisposeAsync();
            }
            if (generation != _automationGeneration) return;
            if (_lifetime.IsCancellationRequested || !Defaults.AutomationEnabled)
            {
                AutomationStatus = AppStrings.Get("AutomationDisabled");
                return;
            }
            var service = new ScriptingService(Accounts,
                () => (App.MainWindow as MainWindow)?.CaptureScriptingSelection() ?? ScriptingSelection.Empty,
                App.OpenScriptingLocation,
                typeof(App).Assembly.GetName().Version?.ToString() ?? "1.0.0");
            _automation = new LocalAutomationPipe(LocalAutomationPipe.NameForCurrentUser(AppConfig.DataDirectory),
                (json, ct) => HandleAutomationAsync(service, json, ct),
                error => App.LogException("Local automation transport", error));
            await _automation.StartAsync();
            _ = ObserveAutomationCompletionAsync(_automation);
            if (generation == _automationGeneration)
                AutomationStatus = AppStrings.Get("AutomationAvailable");
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            AutomationStatus = string.Format(System.Globalization.CultureInfo.CurrentCulture, AppStrings.Get("AutomationStartFailed"), error.Message);
            App.LogException("Start local automation", error);
            if (_automation is not null) { await _automation.DisposeAsync(); _automation = null; }
        }
        finally
        {
            if (generation == _automationGeneration) AutomationStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ObserveAutomationCompletionAsync(LocalAutomationPipe endpoint)
    {
        await endpoint.Completion;
        if (ReferenceEquals(_automation, endpoint) && !_lifetime.IsCancellationRequested && Defaults.AutomationEnabled)
        {
            AutomationStatus = AppStrings.Get("AutomationStopped");
            AutomationStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<string> HandleAutomationAsync(ScriptingService service, string json, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<ScriptingResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_dispatcher?.TryEnqueue(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (_lifetime.IsCancellationRequested || !Defaults.AutomationEnabled)
                    throw new InvalidOperationException("Local automation is disabled or the application is closing.");
                ScriptingRequest request;
                try { request = ScriptingProtocol.Parse(json); }
                catch (Exception error) when (error is JsonException or ArgumentException)
                {
                    completion.TrySetResult(ScriptingProtocol.Failure(null, "invalidRequest", error.Message));
                    return;
                }
                completion.TrySetResult(await service.HandleAsync(request, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); }
            catch (Exception error)
            {
                App.LogException("Local automation", error);
                completion.TrySetResult(ScriptingProtocol.Failure(null, "operationFailed", error.Message));
            }
        }) != true)
            throw new InvalidOperationException("The application dispatcher is unavailable.");
        var response = await completion.Task.ConfigureAwait(false);
        return JsonSerializer.Serialize(response, ScriptingProtocol.Options);
    }

    private async Task StopAutomationAsync()
    {
        await _automationChanges;
        if (_automation is not null)
        {
            await _automation.DisposeAsync();
            _automation = null;
        }
    }
}
