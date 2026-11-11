using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace WinNewsWire.Helpers;

public sealed class ToolbarPanel : Grid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ToolbarPeer(this);

    private sealed class ToolbarPeer(ToolbarPanel owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(ToolbarPanel);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ToolBar;
        protected override bool IsControlElementCore() => true;
    }
}
