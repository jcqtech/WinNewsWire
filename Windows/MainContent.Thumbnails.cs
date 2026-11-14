using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.AppShared;
using WinNewsWire.Articles;
using WinNewsWire.Helpers;
using WinNewsWire.Images;
using WinNewsWire.Services;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private sealed class ThumbnailBinding(long tagCallback)
    {
        internal long TagCallback { get; } = tagCallback;
        internal CancellationTokenSource? Request { get; set; }
        internal bool Loading { get; set; }
        internal Article? Article { get; set; }
        internal bool HasThumbnail { get; set; }
        internal bool ImageRequested { get; set; }
        internal string? Url { get; set; }
        internal ThumbnailMode Mode { get; set; }
    }

    private readonly Dictionary<ThumbnailView, ThumbnailBinding> _timelineThumbnails = [];
    private readonly HashSet<Task> _thumbnailTasks = [];
    private readonly CancellationTokenSource _thumbnailLifetime = new();
    private ArticleThumbnailService? _thumbnailService;
    private INotifyCollectionChanged? _thumbnailItems;
    private bool _thumbnailRefreshQueued;
    private bool _visibleThumbnailRefreshQueued;
    private bool ShowsTimelineThumbnails => !_shuttingDown && !ViewModel.UseColumnLayout && !ViewModel.IsUnifiedLayout
        && ViewModel.TimelineThumbnailsDisplay.ToThumbnailMode() is not null;
    private ThumbnailMode TimelineThumbnailMode => ViewModel.TimelineThumbnailsDisplay.ToThumbnailMode() ?? ThumbnailMode.Small;
#if PARITY_PROBE
    internal CancellationToken ProbeThumbnailRequestToken(ThumbnailView view)
        => _timelineThumbnails[view].Request?.Token
            ?? throw new InvalidOperationException("The thumbnail view has no active request.");

    internal bool ProbeThumbnailRequestIsLoading(ThumbnailView view)
        => _timelineThumbnails.TryGetValue(view, out var binding) && binding.Loading;

    internal bool ProbeThumbnailWorkIsIdle
        => _thumbnailTasks.Count == 0 && !_thumbnailRefreshQueued && !_visibleThumbnailRefreshQueued;

    internal object ProbeTimelineThumbnailState => new
    {
        ShowsTimelineThumbnails,
        First = (ArticleListView.ItemsPanelRoot as ItemsStackPanel)?.FirstVisibleIndex,
        Last = (ArticleListView.ItemsPanelRoot as ItemsStackPanel)?.LastVisibleIndex,
        Bindings = _timelineThumbnails.Select(pair => new
        {
            Article = (pair.Key.Tag as Article)?.ArticleID,
            BoundArticle = pair.Value.Article?.ArticleID,
            pair.Value.Loading, pair.Value.HasThumbnail, pair.Value.ImageRequested,
            HasImage = pair.Key.ImageSource is not null,
            Visibility = pair.Key.Visibility.ToString(),
            Width = double.IsFinite(pair.Key.Width) ? (double?)pair.Key.Width : null,
            Height = double.IsFinite(pair.Key.Height) ? (double?)pair.Key.Height : null,
            Visible = IsVisibleTimelineThumbnail(pair.Key)
        }).ToArray()
    };

    internal void ProbeSetThumbnailService(ArticleThumbnailService service)
    {
        if (_thumbnailService is not null)
        {
            _thumbnailService.ThumbnailAvailable -= TimelineThumbnailAvailable;
            _thumbnailService.ThumbnailDownloadFailed -= TimelineThumbnailDownloadFailed;
            _thumbnailService.NetworkStatusChanged -= TimelineThumbnailNetworkStatusChanged;
        }
        _thumbnailService = service;
        _thumbnailService.ThumbnailAvailable += TimelineThumbnailAvailable;
        _thumbnailService.ThumbnailDownloadFailed += TimelineThumbnailDownloadFailed;
        _thumbnailService.NetworkStatusChanged += TimelineThumbnailNetworkStatusChanged;
        QueueTimelineThumbnailRefresh();
    }
#endif

    private void InitializeTimelineThumbnails()
    {
        ViewModel.TimelineArticlesChanged += TimelineFeedIconArticlesChanged;
        _thumbnailService = AppRuntime.AppService.Shared.ThumbnailImages;
        _thumbnailService.ThumbnailAvailable += TimelineThumbnailAvailable;
        _thumbnailService.ThumbnailDownloadFailed += TimelineThumbnailDownloadFailed;
        _thumbnailService.NetworkStatusChanged += TimelineThumbnailNetworkStatusChanged;
        _articleScrollViewer!.ViewChanged += TimelineThumbnailsViewChanged;
        ArticleListView.LayoutUpdated += TimelineThumbnailsLayoutUpdated;
        ObserveThumbnailItems();
    }

    private void ObserveThumbnailItems()
    {
        QueueTimelineAppearanceUpdate();
        QueueTimelineFeedIconsUpdate();
        if (_thumbnailItems is not null) _thumbnailItems.CollectionChanged -= ThumbnailItemsChanged;
        _thumbnailItems = ViewModel.ArticleItems;
        _thumbnailItems.CollectionChanged += ThumbnailItemsChanged;
        QueueTimelineThumbnailRefresh();
    }

    private void ThumbnailItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        QueueTimelineAppearanceUpdate();
        QueueTimelineFeedIconsUpdate();
        QueueTimelineThumbnailRefresh();
    }

    private void QueueTimelineThumbnailRefresh()
    {
        if (_shuttingDown || _thumbnailService is null) return;
        if (_thumbnailRefreshQueued) return;
        _thumbnailRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _thumbnailRefreshQueued = false;
            if (_shuttingDown) return;
            foreach (var view in _timelineThumbnails.Keys.ToArray()) RefreshTimelineThumbnail(view);
            if (ShowsTimelineThumbnails) QueueVisibleThumbnailRefresh();
        }))
        {
            _thumbnailRefreshQueued = false;
            throw new InvalidOperationException("The timeline thumbnail dispatcher is unavailable.");
        }
    }

    private void TimelineThumbnail_Loaded(object sender, RoutedEventArgs args)
    {
        if (_shuttingDown || sender is not ThumbnailView view || _timelineThumbnails.ContainsKey(view)) return;
        long callback = view.RegisterPropertyChangedCallback(FrameworkElement.TagProperty, (element, _) =>
        {
            if (_timelineThumbnails.TryGetValue(view, out var previous)
                && previous.Article is { } oldArticle && element.GetValue(FrameworkElement.TagProperty) is Article newArticle
                && oldArticle.ArticleID == newArticle.ArticleID && oldArticle.AccountID == newArticle.AccountID
                && !ReferenceEquals(oldArticle, newArticle))
                QueueTimelineFeedIconsUpdate();
            RefreshTimelineThumbnail((ThumbnailView)element);
        });
        _timelineThumbnails.Add(view, new(callback));
        RefreshTimelineThumbnail(view);
        QueueVisibleThumbnailRefresh();
    }

    private void TimelineThumbnail_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is ThumbnailView view) RemoveTimelineThumbnail(view);
    }

    private void RemoveTimelineThumbnail(ThumbnailView view)
    {
        if (!_timelineThumbnails.Remove(view, out var binding)) return;
        binding.Request?.Cancel();
        binding.Request?.Dispose();
        view.UnregisterPropertyChangedCallback(FrameworkElement.TagProperty, binding.TagCallback);
        view.ImageSource = null;
        view.Visibility = Visibility.Collapsed;
    }

    private void RefreshTimelineThumbnail(ThumbnailView view)
    {
        if (!_timelineThumbnails.TryGetValue(view, out var binding)) return;
        binding.Request?.Cancel();
        binding.Request?.Dispose();
        binding.Request = null;
        binding.Loading = false;
        if (!ShowsTimelineThumbnails || _thumbnailService is null || view.Tag is not Article article)
        {
            binding.Article = view.Tag as Article;
            binding.HasThumbnail = false;
            binding.Url = null;
            view.ImageSource = null;
            view.Visibility = Visibility.Collapsed;
            return;
        }
        if (!ReferenceEquals(binding.Article, article))
        {
            binding.Article = article;
            binding.HasThumbnail = false;
            binding.Url = null;
            view.ImageSource = null;
            view.Visibility = Visibility.Collapsed;
        }
        if (binding.Mode != TimelineThumbnailMode)
        {
            binding.Mode = TimelineThumbnailMode;
            view.ImageSource = null;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_thumbnailLifetime.Token);
        binding.Request = cancellation;
        binding.Loading = true;
        binding.ImageRequested = false;
        var token = cancellation.Token;
        TrackThumbnailTask(async () =>
        {
            try { await LoadTimelineThumbnailAsync(view, binding, article, token); }
            finally
            {
                if (ReferenceEquals(binding.Request, cancellation))
                {
                    binding.Loading = false;
                    if (!token.IsCancellationRequested && ShowsTimelineThumbnails && binding.HasThumbnail
                        && !binding.ImageRequested)
                        QueueVisibleThumbnailRefresh();
                }
            }
        });
    }

    private async Task LoadTimelineThumbnailAsync(ThumbnailView view, ThumbnailBinding binding, Article article, CancellationToken ct)
    {
        var mode = TimelineThumbnailMode;
        bool IsCurrent() => !ct.IsCancellationRequested && ShowsTimelineThumbnails && ReferenceEquals(view.Tag, article)
            && mode == TimelineThumbnailMode && _timelineThumbnails.TryGetValue(view, out var current) && ReferenceEquals(current, binding);
        var url = await _thumbnailService!.ThumbnailUrlAsync(article, mode, ct);
        if (!IsCurrent()) return;
        binding.Url = url;
        binding.HasThumbnail = url is not null;
        if (url is null)
        {
            view.ImageSource = null;
            view.Visibility = Visibility.Collapsed;
            return;
        }
        view.Visibility = Visibility.Visible;
        UpdateThumbnailSize(view);
        if (!IsVisibleTimelineThumbnail(view)) return;
        binding.ImageRequested = true;
        var image = await _thumbnailService.ThumbnailAsync(article, mode, ct, resolvedUrl: url);
        if (!IsCurrent()) return;
        view.ImageSource = image;
        if (image is null && await _thumbnailService.ThumbnailUrlAsync(article, mode, ct) is null && IsCurrent())
            view.Visibility = Visibility.Collapsed;
    }

    private void UpdateThumbnailSize(ThumbnailView view)
    {
        if (view.Visibility != Visibility.Visible || view.Parent is not Grid grid) return;
        bool large = TimelineThumbnailMode == ThumbnailMode.Large;
        int largeColumn = grid.Children.OfType<IconView>().Any(icon => icon.Visibility == Visibility.Visible) ? 1 : 2;
        Grid.SetColumn(view, large ? largeColumn : 3);
        Grid.SetColumnSpan(view, large ? 4 - largeColumn : 1);
        Grid.SetRow(view, large ? 1 : 0);
        view.Margin = large ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        if (large)
        {
            view.Width = double.NaN;
            view.Height = 180;
            return;
        }
        view.Width = view.Height = 76;
    }

    private void TimelineThumbnailAvailable(object? sender, string url)
    {
        if (!ShowsTimelineThumbnails) return;
        foreach (var pair in _timelineThumbnails.ToArray())
            if (!pair.Value.Loading && pair.Key.ImageSource is null
                && string.Equals(pair.Value.Url, url, StringComparison.Ordinal)
                && IsVisibleTimelineThumbnail(pair.Key))
                RefreshTimelineThumbnail(pair.Key);
    }

    private void TimelineThumbnailNetworkStatusChanged(object? sender, EventArgs args)
    {
        if (!ShowsTimelineThumbnails) return;
        QueueTimelineThumbnailRefresh();
    }

    private void TimelineThumbnailDownloadFailed(object? sender, ThumbnailUrlFailure failure)
    {
        if (!ShowsTimelineThumbnails) return;
        foreach (var pair in _timelineThumbnails.ToArray())
            if (pair.Key.Tag is Article article && failure.ArticleIds.Contains(article.ArticleID))
                RefreshTimelineThumbnail(pair.Key);
        QueueVisibleThumbnailRefresh();
    }

    private bool IsVisibleTimelineThumbnail(ThumbnailView view)
    {
        if (view.DataContext is not Models.FeedItem item || ArticleListView.ItemsPanelRoot is not ItemsStackPanel panel)
            return false;
        int index = ViewModel.ArticleItems.IndexOf(item);
        return index >= 0 && panel.FirstVisibleIndex >= 0
            && index >= panel.FirstVisibleIndex && index <= panel.LastVisibleIndex;
    }

    private void TimelineThumbnailsViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        QueueVisibleThumbnailRefresh();
    }

    private void TimelineThumbnailsLayoutUpdated(object? sender, object args)
    {
        if (!ShowsTimelineThumbnails || _visibleThumbnailRefreshQueued) return;
        // Replacement rows can acquire their visible index after the first queued refresh.
        if (_timelineThumbnails.Any(pair => pair.Value.HasThumbnail && !pair.Value.Loading && !pair.Value.ImageRequested
            && pair.Key.ImageSource is null && IsVisibleTimelineThumbnail(pair.Key)))
            QueueVisibleThumbnailRefresh();
    }

    private void QueueVisibleThumbnailRefresh()
    {
        if (!ShowsTimelineThumbnails || _visibleThumbnailRefreshQueued) return;
        _visibleThumbnailRefreshQueued = true;
        // Recycled templates can load before the native visible indices settle.
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _visibleThumbnailRefreshQueued = false;
            if (!ShowsTimelineThumbnails) return;
            foreach (var pair in _timelineThumbnails.ToArray())
                if (!pair.Value.Loading && pair.Value.HasThumbnail
                    && pair.Key.ImageSource is null && IsVisibleTimelineThumbnail(pair.Key))
                    RefreshTimelineThumbnail(pair.Key);
        }))
        {
            _visibleThumbnailRefreshQueued = false;
            throw new InvalidOperationException("The visible-thumbnail dispatcher is unavailable.");
        }
    }

    private void TrackThumbnailTask(Func<Task> work)
    {
        var operation = ObserveThumbnailTaskAsync(work);
        _thumbnailTasks.Add(operation);
        _ = RemoveThumbnailTaskAsync(operation);
    }

    private static async Task ObserveThumbnailTaskAsync(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or COMException or InvalidOperationException
            or ArgumentException or NotSupportedException or Microsoft.Data.Sqlite.SqliteException)
        {
            App.LogException("Load timeline thumbnail", error);
        }
    }

    private async Task RemoveThumbnailTaskAsync(Task operation)
    {
        await operation;
        _thumbnailTasks.Remove(operation);
    }

    private async Task StopTimelineThumbnailsAsync()
    {
        ViewModel.TimelineArticlesChanged -= TimelineFeedIconArticlesChanged;
        ArticleListView.LayoutUpdated -= TimelineThumbnailsLayoutUpdated;
        _thumbnailLifetime.Cancel();
        if (_thumbnailItems is not null) _thumbnailItems.CollectionChanged -= ThumbnailItemsChanged;
        if (_thumbnailService is not null)
        {
            _thumbnailService.ThumbnailAvailable -= TimelineThumbnailAvailable;
            _thumbnailService.ThumbnailDownloadFailed -= TimelineThumbnailDownloadFailed;
            _thumbnailService.NetworkStatusChanged -= TimelineThumbnailNetworkStatusChanged;
        }
        if (_articleScrollViewer is not null) _articleScrollViewer.ViewChanged -= TimelineThumbnailsViewChanged;
        foreach (var view in _timelineThumbnails.Keys.ToArray()) RemoveTimelineThumbnail(view);
        await Task.WhenAll(_thumbnailTasks.ToArray());
        _thumbnailLifetime.Dispose();
    }
}
