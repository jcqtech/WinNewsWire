using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using WinNewsWire.AppRuntime;
using WinNewsWire.ErrorLog;

namespace WinNewsWire.AppWindows;

public sealed partial class ErrorLogWindow : Window
{
    public sealed class Row
    {
        public ErrorLogEntry Entry { get; }
        public Row(ErrorLogEntry e) { Entry = e; }
        public string Header => $"{Entry.Date.ToLocalTime():g} — {Entry.SourceName} — {Entry.Operation}";
        public string ErrorMessage => Entry.ErrorMessage;
    }

    public ErrorLogWindow()
    {
        InitializeComponent();
        WindowIconHelper.ApplyFlatIcon(this);
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            var entries = await AppService.Shared.ErrorLog.FetchRecentAsync();
            EntriesList.ItemsSource = entries.Select(e => new Row(e)).ToList();
        }
        catch { }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        await AppService.Shared.ErrorLog.ClearAsync();
        await ReloadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
