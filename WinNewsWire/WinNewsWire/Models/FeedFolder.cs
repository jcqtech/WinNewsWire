using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinNewsWire.Models;

public partial class FeedFolder : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isExpanded = true;

    public ObservableCollection<Feed> Feeds { get; } = new();

    public int UnreadCount
    {
        get
        {
            var count = 0;
            foreach (var feed in Feeds)
                count += feed.UnreadCount;
            return count;
        }
    }
}
