using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WinNewsWire.AppShared;

namespace WinNewsWire.Helpers;

public sealed class CopyableLabel : UserControl
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(CopyableLabel), new PropertyMetadata("", (owner, args) =>
        {
            var label = (CopyableLabel)owner;
            label._text.Text = (string)args.NewValue;
            AutomationProperties.SetName(label, label._text.Text);
        }));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetValue(TextProperty, value);
        }
    }

#if PARITY_PROBE
    internal Action<DataPackage>? ProbeClipboardWriter { get; set; }
#endif

    public CopyableLabel()
    {
        Content = _text;
        IsTabStop = true;
        var copy = new MenuFlyoutItem { Text = AppStrings.Get("Copy") };
        copy.Click += Copy_Click;
        var menu = new MenuFlyout();
        menu.Items.Add(copy);
        ContextFlyout = menu;
    }

    private async void Copy_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetText(Text);
#if PARITY_PROBE
            if (ProbeClipboardWriter is { } writer) writer(data);
            else
#endif
                Clipboard.SetContent(data);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            App.LogException("Copy label text", error);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = AppStrings.Get("CopyTextError"),
                Content = error.Message,
                CloseButtonText = AppStrings.Get("Close")
            };
            AppWindows.WindowThemeHelper.Apply(dialog);
            await dialog.ShowAsync();
        }
    }
}
