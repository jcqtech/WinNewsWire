using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinNewsWire.Models;

public partial class Feed : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _feedUrl = string.Empty;

    [ObservableProperty]
    private string _siteUrl = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _iconUrl = string.Empty;

    [ObservableProperty]
    private int _unreadCount;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    public ObservableCollection<FeedItem> Items { get; } = new();
}
