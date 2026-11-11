using WinNewsWire.Articles;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.Commands;

/// <summary>Port of <c>MarkStatusCommand</c>.</summary>
public sealed class MarkStatusCommand : IUndoableCommand
{
    private readonly ArticleStatus.Key _statusKey;
    private readonly bool _flag;
    private readonly HashSet<Article> _articles;
    private readonly Func<IReadOnlyCollection<Article>, ArticleStatus.Key, bool, Task> _apply;

    public string UndoActionName { get; }
    public string RedoActionName { get; }

    public static MarkStatusCommand? Create(
        IEnumerable<Article> initialArticles, ArticleStatus.Key key, bool flag,
        Func<IReadOnlyCollection<Article>, ArticleStatus.Key, bool, Task> apply, Func<Article, bool>? canMarkUnread = null)
    {
        var filtered = initialArticles.DistinctBy(a => (a.AccountID, a.ArticleID))
            .Where(a => a.Status.BoolStatus(key) != flag)
            .Where(a => key != ArticleStatus.Key.Read || flag || canMarkUnread?.Invoke(a) != false).ToHashSet();
        if (filtered.Count == 0) return null;
        return new MarkStatusCommand(filtered, key, flag, apply);
    }

    private MarkStatusCommand(HashSet<Article> articles, ArticleStatus.Key key, bool flag, Func<IReadOnlyCollection<Article>, ArticleStatus.Key, bool, Task> apply)
    {
        _articles = articles; _statusKey = key; _flag = flag; _apply = apply;
        var name = ActionName(key, flag);
        UndoActionName = name; RedoActionName = name;
    }

    public Task PerformAsync() => MarkAsync(_flag);
    public Task UndoAsync() => MarkAsync(!_flag);
    public Task RedoAsync() => MarkAsync(_flag);

    private async Task MarkAsync(bool value)
    {
        try { await _apply(_articles, _statusKey, value); }
        catch (Exception original)
        {
            if (_articles.Any(article => article.Status.BoolStatus(_statusKey) == value))
            {
                try { await _apply(_articles, _statusKey, !value); }
                catch (Exception rollback) { throw new AggregateException("The status change and its rollback both failed.", original, rollback); }
                foreach (var article in _articles) article.Status.SetBoolStatus(!value, _statusKey);
            }
            throw;
        }
        foreach (var a in _articles) a.Status.SetBoolStatus(value, _statusKey);
    }

    private static string ActionName(ArticleStatus.Key key, bool flag) => (key, flag) switch
    {
        (ArticleStatus.Key.Read, true) => "Mark Read",
        (ArticleStatus.Key.Read, false) => "Mark Unread",
        (ArticleStatus.Key.Starred, true) => "Mark Starred",
        (ArticleStatus.Key.Starred, false) => "Mark Unstarred",
        _ => "Mark",
    };
}
