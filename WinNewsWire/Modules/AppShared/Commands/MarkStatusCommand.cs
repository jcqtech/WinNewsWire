using WinNewsWire.Articles;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.Commands;

/// <summary>Port of <c>MarkStatusCommand</c>.</summary>
public sealed class MarkStatusCommand : IUndoableCommand
{
    private readonly ArticleStatus.Key _statusKey;
    private readonly bool _flag;
    private readonly HashSet<Article> _articles;
    private readonly Func<IEnumerable<string>, ArticleStatus.Key, bool, Task> _apply;

    public string UndoActionName { get; }
    public string RedoActionName { get; }

    public static MarkStatusCommand? Create(
        IEnumerable<Article> initialArticles, ArticleStatus.Key key, bool flag,
        Func<IEnumerable<string>, ArticleStatus.Key, bool, Task> apply)
    {
        var filtered = initialArticles.Where(a => a.Status.BoolStatus(key) != flag).ToHashSet();
        if (filtered.Count == 0) return null;
        return new MarkStatusCommand(filtered, key, flag, apply);
    }

    private MarkStatusCommand(HashSet<Article> articles, ArticleStatus.Key key, bool flag, Func<IEnumerable<string>, ArticleStatus.Key, bool, Task> apply)
    {
        _articles = articles; _statusKey = key; _flag = flag; _apply = apply;
        var name = ActionName(key, flag);
        UndoActionName = name; RedoActionName = name;
    }

    public void Perform() => Mark(_flag);
    public void Undo() => Mark(!_flag);
    public void Redo() => Mark(_flag);

    private void Mark(bool value)
    {
        foreach (var a in _articles) a.Status.SetBoolStatus(value, _statusKey);
        _ = _apply(_articles.Select(a => a.ArticleID), _statusKey, value);
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
