using System;
using Microsoft.UI.Xaml;

namespace WinNewsWire.AppWindows;

public sealed partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        WindowIconHelper.ApplyFlatIcon(this);
        WindowThemeHelper.Attach(this);
        ResizeToContent();
        VersionText.Text = $"Version {App.DisplayVersion}";
    }

    private void ResizeToContent()
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            // Bumped from 440x380 to fit the new credits panel comfortably.
            appWindow.Resize(new Windows.Graphics.SizeInt32(520, 680));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"AboutWindow resize: {ex.Message}"); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
