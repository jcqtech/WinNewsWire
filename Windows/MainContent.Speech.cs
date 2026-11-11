using System;
using System.Text.Json;
using System.Threading.Tasks;
using WinNewsWire.AppShared;

namespace WinNewsWire;

public sealed partial class MainContent
{
    public async Task StartSpeakingAsync()
    {
        if (_shuttingDown || !_webViewReady || !HasDisplayedDocument) return;
        ulong navigation = ActiveNavigationID;
        var browser = ActiveWebView;
        try
        {
            string? text = JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("""
                (() => {
                    const selected = window.getSelection()?.toString() ?? "";
                    if (selected.trim().length > 0) return selected;
                    return (document.getElementById("bodyContainer") ?? document.body)?.innerText ?? "";
                })()
                """));
            if (_shuttingDown || navigation != ActiveNavigationID || !ReferenceEquals(browser, ActiveWebView)) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                ViewModel.StatusMessage = AppStrings.Get("NoSpeechText");
                return;
            }
            await AppRuntime.AppService.Shared.Speech.SpeakAsync(this, text);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException
            or UnauthorizedAccessException or NotSupportedException or JsonException or ArgumentException or System.IO.IOException)
        {
            App.LogException("Speak article", error);
            if (!_shuttingDown) ViewModel.StatusMessage = AppStrings.Get("SpeechFailed") + " " + error.Message;
        }
    }
}
