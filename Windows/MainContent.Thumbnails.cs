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
    }

    private readonly Dictionary<ThumbnailView, ThumbnailBinding> _timelineThumbnails = [];
    private readonly HashSet<Task> _thumbnailTasks = [];
    private readonly CancellationTokenSource _thumbnailLifetime = new();
    private ArticleThumbnailService? _thumbnailService;
    private INotifyCollectionChanged? _thumbnailItems;
    private bool _thumbnailRefreshQueued;
    private bool _visibleThumbnailRefreshQueued;
    private (int Start, int End)? _thumbnailLookahead;
    private bool ShowsTimelineThumbnails => !_shuttingDown && !ViewModel.UseColumnLayout && !ViewModel.IsUnifiedLayout;
#if PARITY_PROBE
    internal object ProbeTimelineThumbnailState => new
    {
        ShowsTimelineThumbnails,
        First = (ArticleListView.ItemsPanelRoot as ItemsStackPanel)?.FirstVisibleIndex,
        Last = (ArticleListView.ItemsPanelRoot as ItemsStackPanel)?.LastVisibleIndex,
        Lookahead = _thumbnailLookahead?.ToString(),
        Bindings = _timelineThumbnails.Select(pair => new
        {
            Article = (pair.Key.Tag as Article)?.ArticleID,
            BoundArticle = pair.Value.Article?.ArticleID,
            pair.Value.Loading, pair.Value.HasThumbnail,
            HasImage = pair.Key.ImageSource is not null,
            Visibility = pair.Key.Visibility.ToString(),
            pair.Key.Width, pair.Key.Height,
            Visible = IsVisibleTimelineThumbnail(pair.Key)
        }).ToArray()
    };

    internal void ProbeSetThumbnailService(ArticleThumbnailService service)
    {
        if (_thumbnailService is not null)
        {
            _thumbnailService.ThumbnailUrlsAvailable -= TimelineThumbnailUrlsAvailable;
            _thumbnailService.ThumbnailDownloadFailed -= TimelineThumbnailDownloadFailed;
        }
        _thumbnailService = service;
        _thumbnailService.ThumbnailUrlsAvailable += TimelineThumbnailUrlsAvailable;
        _thumbnailService.ThumbnailDownloadFailed += TimelineThumbnailDownloadFailed;
        QueueTimelineThumbnailRefresh();
    }
#endif

    private void InitializeTimelineThumbnails()
    {
        _thumbnailService = AppRuntime.AppService.Shared.ThumbnailImages;
        _thumbnailService.ThumbnailUrlsAvailable += TimelineThumbnailUrlsAvailable;
        _thumbnailService.ThumbnailDownloadFailed += TimelineThumbnailDownloadFailed;
        _articleScrollViewer!.ViewChanged += TimelineThumbnailsViewChanged;
        ObserveThumbnailItems();
    }

    private void ObserveThumbnailItems()
    {
        if (_thumbnailItems is not null) _thumbnailItems.CollectionChanged -= ThumbnailItemsChanged;
        _thumbnailItems = ViewModel.ArticleItems;
        _thumbnailItems.CollectionChanged += ThumbnailItemsChanged;
        QueueTimelineThumbnailRefresh();
    }

    private void ThumbnailItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => QueueTimelineThumbnailRefresh();

    private void QueueTimelineThumbnailRefresh()
    {
        if (_shuttingDown || _thumbnailService is null || _thumbnailRefreshQueued) return;
        _thumbnailRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _thumbnailRefreshQueued = false;
            if (_shuttingDown) return;
            _thumbnailLookahead = null;
            foreach (var view in _timelineThumbnails.Keys.ToArray()) RefreshTimelineThumbnail(view);
            if (!ShowsTimelineThumbnails) return;
            var articles = ViewModel.ArticleItems.Select(item => item.SourceArticle).OfType<Article>().ToArray();
            if (articles.Length > 0) TrackThumbnailTask(() => _thumbnailService.DiscoverAsync(articles, _thumbnailLifetime.Token));
            PrefetchTimelineThumbnails();
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
            RefreshTimelineThumbnail((ThumbnailView)element));
        _timelineThumbnails.Add(view, new(callback));
        if (view.Parent is Grid grid && grid.Children.OfType<StackPanel>().FirstOrDefault() is { } text)
            text.SizeChanged += ThumbnailTextSizeChanged;
        RefreshTimelineThumbnail(view);
        PrefetchTimelineThumbnails();
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
        if (view.Parent is Grid grid && grid.Children.OfType<StackPanel>().FirstOrDefault() is { } text)
            text.SizeChanged -= ThumbnailTextSizeChanged;
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
            binding.Article = null;
            binding.HasThumbnail = false;
            view.ImageSource = null;
            view.Visibility = Visibility.Collapsed;
            return;
        }
        if (!ReferenceEquals(binding.Article, article))
        {
            binding.Article = article;
            binding.HasThumbnail = false;
            view.ImageSource = null;
            view.Visibility = Visibility.Collapsed;
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
        bool IsCurrent() => !ct.IsCancellationRequested && ShowsTimelineThumbnails && ReferenceEquals(view.Tag, article)
            && _timelineThumbnails.TryGetValue(view, out var current) && ReferenceEquals(current, binding);
        var url = await _thumbnailService!.ThumbnailUrlAsync(article, ThumbnailMode.Small, ct);
        if (!IsCurrent()) return;
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
        var image = await _thumbnailService.ThumbnailAsync(article, ThumbnailMode.Small, ct);
        if (!IsCurrent()) return;
        view.ImageSource = image;
        if (image is null && await _thumbnailService.ThumbnailUrlAsync(article, ThumbnailMode.Small, ct) is null && IsCurrent())
            view.Visibility = Visibility.Collapsed;
    }

    private static void UpdateThumbnailSize(ThumbnailView view)
    {
        if (view.Visibility != Visibility.Visible || view.Parent is not Grid grid) return;
        double iconHeight = grid.Children.OfType<IconView>().FirstOrDefault()?.ActualHeight ?? 0;
        double textHeight = grid.Children.OfType<StackPanel>().FirstOrDefault()?.ActualHeight ?? 0;
        double dimension = Math.Min(76, Math.Max(iconHeight, textHeight));
        view.Width = view.Height = dimension;
    }

    private void ThumbnailTextSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is StackPanel { Parent: Grid grid })
            foreach (var view in grid.Children.OfType<ThumbnailView>()) UpdateThumbnailSize(view);
    }

    private void TimelineThumbnailUrlsAvailable(object? sender, IReadOnlySet<string> ids)
    {
        if (!ShowsTimelineThumbnails) return;
        foreach (var view in _timelineThumbnails.Keys.ToArray())
            if (view.Tag is Article article && ids.Contains(article.ArticleID)) RefreshTimelineThumbnail(view);
        _thumbnailLookahead = null;
        PrefetchTimelineThumbnails();
    }

    private void TimelineThumbnailDownloadFailed(object? sender, string url)
    {
        if (!ShowsTimelineThumbnails) return;
        foreach (var view in _timelineThumbnails.Keys.ToArray())
            if (IsVisibleTimelineThumbnail(view)) RefreshTimelineThumbnail(view);
        _thumbnailLookahead = null;
        PrefetchTimelineThumbnails();
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
        PrefetchTimelineThumbnails();
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
            PrefetchTimelineThumbnails();
        }))
        {
            _visibleThumbnailRefreshQueued = false;
            throw new InvalidOperationException("The visible-thumbnail dispatcher is unavailable.");
        }
    }

    private void PrefetchTimelineThumbnails()
    {
        if (!ShowsTimelineThumbnails || _thumbnailService is null
            || ArticleListView.ItemsPanelRoot is not ItemsStackPanel panel || panel.FirstVisibleIndex < 0) return;
        int count = panel.LastVisibleIndex - panel.FirstVisibleIndex + 1;
        int start = panel.LastVisibleIndex + 1;
        int end = Math.Min(ViewModel.ArticleItems.Count, start + count);
        if (count <= 0 || start < 0 || start >= end || _thumbnailLookahead == (start, end)) return;
        _thumbnailLookahead = (start, end);
        for (int index = start; index < end; index++)
            if (ViewModel.ArticleItems[index].SourceArticle is { } article)
                TrackThumbnailTask(async () => { await _thumbnailService.ThumbnailAsync(article, ThumbnailMode.Small, _thumbnailLifetime.Token); });
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
        _thumbnailLifetime.Cancel();
        if (_thumbnailItems is not null) _thumbnailItems.CollectionChanged -= ThumbnailItemsChanged;
        if (_thumbnailService is not null)
        {
            _thumbnailService.ThumbnailUrlsAvailable -= TimelineThumbnailUrlsAvailable;
            _thumbnailService.ThumbnailDownloadFailed -= TimelineThumbnailDownloadFailed;
        }
        if (_articleScrollViewer is not null) _articleScrollViewer.ViewChanged -= TimelineThumbnailsViewChanged;
        foreach (var view in _timelineThumbnails.Keys.ToArray()) RemoveTimelineThumbnail(view);
        await Task.WhenAll(_thumbnailTasks.ToArray());
        _thumbnailLifetime.Dispose();
    }
}
