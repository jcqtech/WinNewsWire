#if PARITY_PROBE
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppShared;
using WinNewsWire.Core;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckComAutomationAsync(MainWindow window)
    {
        window.Activate();
        AppRuntime.AppService.Shared.Start();
        await window.ContentInitialization;
        await WaitFor(() => Task.FromResult(window.ProbeContent.IsLoaded && window.ProbeContent.ProbeWebViewReady),
            "COM scenario reader initialization");
        var preferences = new AppWindows.PreferencesWindow();
        try
        {
            preferences.Activate();
            await WaitFor(() => Task.FromResult(preferences.Content is FrameworkElement { IsLoaded: true }), "COM scenario preferences");
            Descendants(preferences.Content).OfType<Expander>().Single(control => control.Name == "AdvancedOptionsExpander").IsExpanded = true;
            await WaitFor(() => Task.FromResult(Descendants(preferences.Content).OfType<ToggleSwitch>()
                .Any(control => control.Name == "AutomationToggle" && control.IsLoaded)), "COM scenario consent control");
            Descendants(preferences.Content).OfType<ToggleSwitch>().Single(control => control.Name == "AutomationToggle").IsOn = true;
        }
        finally { preferences.Close(); }
        await WaitFor(() => Task.FromResult(AppRuntime.AppService.Shared.AutomationStatus == AppStrings.Get("AutomationAvailable")),
            "COM scenario endpoint");
        window.Activate();
        string? package = null;
        try { package = global::Windows.ApplicationModel.Package.Current.Id.FullName; }
        catch (InvalidOperationException) { }
        File.WriteAllText(Path.Combine(OutputDirectory, "com-ready.json"), JsonSerializer.Serialize(new
        {
            ProcessID = Environment.ProcessId, Package = package, Directory = AppConfig.DataDirectory,
            ExecutableDirectory = AppContext.BaseDirectory
        }));
        string? previous = null;
        int commands = 0;
        var timeout = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < timeout)
        {
            await Task.Delay(100);
            string path = Path.Combine(OutputDirectory, "com-command.json");
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var request = document.RootElement;
            string id = request.GetProperty("ID").GetString()!;
            if (id == previous) continue;
            previous = id;
            string action = request.GetProperty("Action").GetString()!;
            object? details = null;
            string? error = null;
            try
            {
                var account = AccountManager.Shared.DefaultAccount;
                var active = (App.MainWindow as MainWindow)!;
                if (action is "select" or "secondWindow")
                {
                    if (action == "secondWindow")
                    {
                        active = App.OpenMainWindow();
                        await active.ContentInitialization;
                        await WaitFor(() => Task.FromResult(active.ProbeContent.ProbeWebViewReady), "Second COM scenario window");
                    }
                    var feed = account.FlattenedFeeds().Single(item => item.Url == request.GetProperty("FeedUrl").GetString());
                    var model = active.ProbeContent.ViewModel;
                    model.SetSelectedSidebarItems([model.FindSidebarItemForFeed(account.AccountID, feed.FeedID)!]);
                    await model.FilterArticlesAsync();
                    await model.WaitForCurrentTimelineAsync(default);
                    var selected = model.ArticleItems.Skip(request.GetProperty("Ordinal").GetInt32() - 1).First();
                    model.SelectedArticle = selected;
                    model.SetSelectedArticles([selected]);
                    active.Activate();
                    await WaitFor(() => Task.FromResult(active.ProbeContent.ProbeArticleReady), "Selected COM scenario article");
                    details = new { ID = selected.SourceArticle!.UniqueID, selected.Title, Windows = App.MainWindows.Count };
                }
                else if (action == "state")
                {
                    var selected = active.ProbeContent.ViewModel.SelectedArticle;
                    details = new
                    {
                        ID = selected?.SourceArticle?.UniqueID, IsRead = selected?.IsRead, IsStarred = selected?.IsStarred,
                        ArticleCount = active.ProbeContent.ViewModel.ArticleItems.Count,
                        Feeds = account.FlattenedFeeds().Select(feed => new { feed.Url, feed.FeedID }).ToArray()
                    };
                }
                else if (action == "refresh")
                {
                    var feed = account.FlattenedFeeds().Single(item => item.Url == request.GetProperty("FeedUrl").GetString());
                    if (!Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri) || !uri.IsLoopback)
                        throw new ArgumentException("Only a local scenario feed may have its refresh timestamp aged.");
                    feed.LastCheckDate = DateTime.UtcNow.AddHours(-1);
                    feed.CacheControlInfo = null;
                    await account.RefreshAllAsync();
                    details = new { Refreshed = true, SampleFeedMadeDue = true };
                }
                else if (action == "stop")
                {
                    foreach (var other in App.MainWindows.Where(item => !ReferenceEquals(item, window)).ToArray())
                        await App.CloseMainWindowAsync(other);
                    details = new { Stopped = true };
                }
                else throw new ArgumentException("Unknown COM scenario action: " + action);
            }
            catch (Exception failure)
            {
                error = failure.ToString();
                App.LogException("COM scenario command", failure);
            }
            var reply = Path.Combine(OutputDirectory, "com-reply.json");
            File.WriteAllText(reply + ".tmp", JsonSerializer.Serialize(new { ID = id, Details = details, Error = error }));
            File.Move(reply + ".tmp", reply, overwrite: true);
            commands++;
            if (error is not null) throw new InvalidOperationException(error);
            if (action == "stop") return new { Commands = commands, Package = package, PrivateProfile = AppConfig.DataDirectory };
        }
        throw new TimeoutException("The external COM scenarios did not finish within twenty minutes.");
    }
}
#endif
