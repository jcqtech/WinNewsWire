using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private List<Row> _allRows = new();

    public ErrorLogWindow()
    {
        InitializeComponent();
        WindowIconHelper.ApplyFlatIcon(this);
        WindowThemeHelper.Attach(this);
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            var entries = await AppService.Shared.ErrorLog.FetchRecentAsync();
            _allRows = entries.Select(e => new Row(e)).ToList();
            ApplyFilter(SearchBox?.Text);
        }
        catch { }
    }

    private void ApplyFilter(string? query)
    {
        IEnumerable<Row> filtered = _allRows;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            filtered = _allRows.Where(r =>
                (r.Header?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (r.ErrorMessage?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        var list = filtered.ToList();
        EntriesList.ItemsSource = list;
        ResultsCount.Text = string.IsNullOrWhiteSpace(query)
            ? $"{list.Count} entries"
            : $"{list.Count} of {_allRows.Count}";
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ApplyFilter(sender.Text);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        await AppService.Shared.ErrorLog.ClearAsync();
        await ReloadAsync();
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var rows = EntriesList.ItemsSource as IEnumerable<Row>;
        if (rows is null) return;
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.AppendLine(r.Header);
            if (!string.IsNullOrWhiteSpace(r.ErrorMessage)) sb.AppendLine(r.ErrorMessage);
            sb.AppendLine(new string('-', 60));
        }
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(sb.ToString());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

