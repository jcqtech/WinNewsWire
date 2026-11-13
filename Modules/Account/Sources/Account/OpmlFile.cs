using WinNewsWire.Parsers;
using WinNewsWire.Core;
using System.Diagnostics;

namespace WinNewsWire.Account;

/// <summary>
/// Port of NetNewsWire's <c>OPMLFile</c>. Wraps a <c>Subscriptions.opml</c>
/// file living next to an account on disk; supports batch import from disk
/// and coalesced autosave when the account's feed/folder set changes.
/// </summary>
/// <remarks>
/// <para>
/// Local accounts restore their subscription structure from OPML at launch.
/// Settings.json retains metadata, custom names, and compatibility with older
/// installations. A malformed OPML file leaves that fallback structure intact.
/// </para>
/// <para>
/// Autosaves coalesce inside the active <see cref="Account.PerformBatchUpdate"/>
/// scope — a single Import → re-export round trip writes the file once,
/// matching the Mac app's <c>CoalescingQueue(interval: 0.5)</c> behaviour.
/// </para>
/// </remarks>
public sealed class OpmlFile : IDisposable
{
    private readonly Account _account;
    private readonly HashSet<Feed> _observedFeeds = new(ReferenceEqualityComparer.Instance);
    public string FilePath { get; }

    public OpmlFile(string filePath, Account account)
    {
        FilePath = filePath;
        _account = account;
        _account.AccountStructureChanged += OnAccountStructureChanged;
        ObserveFeeds();
    }

    /// <summary>
    /// Parses <see cref="FilePath"/> (if it exists) and applies the contained
    /// outlines to the associated account inside a single
    /// <see cref="Account.PerformBatchUpdate(Action)"/> scope, so subscribers
    /// see one structural change instead of one per item.
    /// </summary>
    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var doc = OpmlParser.Parse(new ParserData(FilePath, File.ReadAllBytes(FilePath)));
            _account.RestoreOpmlItems(doc.Children);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FeedParserException)
        {
            Trace.TraceError($"Could not restore OPML {FilePath}: {ex}");
        }
    }

    /// <summary>Persists the account's current feed/folder structure to
    /// <see cref="FilePath"/> as an OPML 1.1 document.</summary>
    public void Save()
    {
        try
        {
            FileUtilities.WriteAllTextAtomically(FilePath, _account.ExportOpml(allowCustomAttributes: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"Could not save OPML {FilePath}: {ex}");
            throw;
        }
    }

    private void ObserveFeeds()
    {
        var current = _account.FlattenedFeeds().ToHashSet<Feed>(ReferenceEqualityComparer.Instance);
        foreach (var feed in _observedFeeds.ToArray())
            if (!current.Contains(feed))
            {
                feed.DisplayNameChanged -= OnFeedDisplayNameChanged;
                _observedFeeds.Remove(feed);
            }
        foreach (var feed in current)
            if (_observedFeeds.Add(feed)) feed.DisplayNameChanged += OnFeedDisplayNameChanged;
    }

    private void OnFeedDisplayNameChanged(object? sender, EventArgs e)
    {
        if (!_account.IsPerformingBatchUpdate) Save();
    }

    private void OnAccountStructureChanged(object? sender, EventArgs e)
    {
        ObserveFeeds();
        Save();
    }

    public void Dispose()
    {
        _account.AccountStructureChanged -= OnAccountStructureChanged;
        foreach (var feed in _observedFeeds) feed.DisplayNameChanged -= OnFeedDisplayNameChanged;
        _observedFeeds.Clear();
    }
}
