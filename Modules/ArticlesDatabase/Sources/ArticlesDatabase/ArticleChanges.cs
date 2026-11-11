using WinNewsWire.Articles;

namespace WinNewsWire.ArticlesDatabase;

public sealed record ArticleChanges(
    IReadOnlySet<Article>? New = null,
    IReadOnlySet<Article>? Updated = null,
    IReadOnlySet<Article>? Deleted = null);
