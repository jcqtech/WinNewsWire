using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Models;

namespace WinNewsWire.Helpers;

/// <summary>Picks between a "section heading" template (Smart Feeds, On My PC, …) and
/// the regular row template based on <see cref="SidebarItem.ItemType"/>. Section
/// headings use static labels while folders retain disclosure controls.</summary>
public sealed class SidebarItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SectionHeaderTemplate { get; set; }
    public DataTemplate? DefaultTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        if (item is SidebarItem si &&
            si.ItemType == SidebarItemType.SectionHeader)
        {
            return SectionHeaderTemplate ?? DefaultTemplate!;
        }
        return DefaultTemplate!;
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
