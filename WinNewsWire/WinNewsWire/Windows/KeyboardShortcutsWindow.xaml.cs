using Microsoft.UI.Xaml;

namespace WinNewsWire.AppWindows;

public sealed partial class KeyboardShortcutsWindow : Window
{
    public KeyboardShortcutsWindow() { InitializeComponent(); WindowIconHelper.ApplyFlatIcon(this); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
