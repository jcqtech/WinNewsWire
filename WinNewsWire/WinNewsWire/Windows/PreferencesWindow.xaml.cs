using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppShared.Timer;
using WinNewsWire.Core;
using WinNewsWire.Dialogs;

namespace WinNewsWire.AppWindows;

public sealed partial class PreferencesWindow : Window
{
    private bool _loaded;

    public PreferencesWindow()
    {
        InitializeComponent();
        WindowIconHelper.ApplyFlatIcon(this);
        PopulateCombos();
        _loaded = true;
    }

    private void PopulateCombos()
    {
        foreach (var v in Enum.GetValues<RefreshInterval>())
            RefreshIntervalCombo.Items.Add(new ComboBoxItem { Content = v.DisplayString(), Tag = (int)v });
        RefreshIntervalCombo.SelectedIndex = Enum.GetValues<RefreshInterval>()
            .ToList().FindIndex(v => (int)v == AppDefaults.Shared.RefreshIntervalRaw);
        if (RefreshIntervalCombo.SelectedIndex < 0) RefreshIntervalCombo.SelectedIndex = 0;

        foreach (var n in new[] { "Small", "Medium", "Large", "Very Large" })
            TextSizeCombo.Items.Add(new ComboBoxItem { Content = n });
        TextSizeCombo.SelectedIndex = Math.Clamp(AppDefaults.Shared.ArticleTextSizeRaw, 0, 3);

        GroupByFeedToggle.IsOn = AppDefaults.Shared.TimelineGroupByFeed;
        OpenBackgroundToggle.IsOn = AppDefaults.Shared.OpenInBrowserInBackground;

        ReaderModeDefaultToggle.IsOn = AppDefaults.Shared.ReaderModeDefault;
        JavascriptToggle.IsOn = AppDefaults.Shared.ArticleContentJavascriptEnabled;
        DebugMenuToggle.IsOn = AppDefaults.Shared.ShowDebugMenu;

        ReloadAccounts();
    }

    public sealed record AccountRow(Account.Account Account)
    {
        public string Name => Account.Name;
        public string TypeLabel => Account.Type.ToString();
    }

    private void ReloadAccounts()
    {
        var rows = AppService.Shared.Accounts.Accounts.Select(a => new AccountRow(a)).ToList();
        AccountsList.ItemsSource = rows;
        if (rows.Count > 0 && AccountsList.SelectedIndex < 0)
            AccountsList.SelectedIndex = 0;
        UpdateSelectedAccountText();
    }

    private void UpdateSelectedAccountText()
    {
        var rows = AccountsList.ItemsSource as System.Collections.Generic.IList<AccountRow>;
        if (AccountsList.SelectedItem is AccountRow row)
            SelectedAccountText.Text = row.Name;
        else if (rows is null || rows.Count == 0)
            SelectedAccountText.Text = "None";
        else
            SelectedAccountText.Text = $"{rows.Count} account{(rows.Count == 1 ? "" : "s")}";
    }

    private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelectedAccountText();

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var created = await WinNewsWire.Dialogs.AccountSheets.AccountTypePickerDialog.ShowAsync(((FrameworkElement)((Button)sender)).XamlRoot);
        if (created is not null) ReloadAccounts();
    }

    private void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is AccountRow row)
        {
            AppService.Shared.Accounts.RemoveAccount(row.Account);
            ReloadAccounts();
        }
    }

    private void RefreshIntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || RefreshIntervalCombo.SelectedItem is not ComboBoxItem item || item.Tag is not int raw) return;
        AppDefaults.Shared.RefreshIntervalRaw = raw;
    }

    private void TextSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        AppDefaults.Shared.ArticleTextSizeRaw = TextSizeCombo.SelectedIndex;
    }

    private void GroupByFeed_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        AppDefaults.Shared.TimelineGroupByFeed = GroupByFeedToggle.IsOn;
    }

    private void OpenBackground_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        AppDefaults.Shared.OpenInBrowserInBackground = OpenBackgroundToggle.IsOn;
    }

    private void Advanced_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        AppDefaults.Shared.ReaderModeDefault = ReaderModeDefaultToggle.IsOn;
        AppDefaults.Shared.ArticleContentJavascriptEnabled = JavascriptToggle.IsOn;
        AppDefaults.Shared.ShowDebugMenu = DebugMenuToggle.IsOn;
    }

    private void SendFeedback_Click(object sender, RoutedEventArgs e)
        => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("mailto:feedback@winnewswire.com?subject=WinNewsWire%20Feedback"));

    private void Help_Click(object sender, RoutedEventArgs e)
        => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("https://winnewswire.com/help/"));
}
