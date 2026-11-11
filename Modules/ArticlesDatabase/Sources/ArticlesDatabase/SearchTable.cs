using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WinNewsWire.Articles;
using WinNewsWire.Core;

namespace WinNewsWire.ArticlesDatabase;

internal static class SearchTable
{
    private static readonly Regex Words = new(@"[\p{L}\p{N}\p{M}_]+", RegexOptions.Compiled);

    public static string SqliteSearchString(string query)
    {
        var words = Words.Matches(query).Select(match => match.Value).ToList();
        var terms = new List<string>(words.Count);
        bool afterTerm = false;
        for (int i = 0; i < words.Count; i++)
        {
            bool isOperator = words[i] is "AND" or "OR";
            if (isOperator && afterTerm && i + 1 < words.Count && words[i + 1] is not ("AND" or "OR"))
            {
                terms.Add(words[i]);
                afterTerm = false;
            }
            else
            {
                terms.Add(words[i] + "*");
                afterTerm = true;
            }
        }
        return string.Join(" ", terms);
    }

    public static void IndexArticle(SqliteConnection connection, Article article)
    {
        using var existing = connection.CreateCommand();
        existing.CommandText = "SELECT searchRowID FROM articles WHERE articleID = @id;";
        existing.Parameters.AddWithValue("@id", article.ArticleID);
        var rowID = existing.ExecuteScalar();

        var body = article.ContentHtml.NilIfEmpty() ?? article.ContentText.NilIfEmpty() ?? article.Summary ?? "";
        body = WebUtility.HtmlDecode(body).StripHtml();
        if (article.Authors is { Count: > 0 })
            body += " " + string.Join(" ", article.Authors.Select(a => a.Name));

        using var index = connection.CreateCommand();
        index.CommandText = "INSERT OR REPLACE INTO search (rowid, title, body) VALUES (@row, @title, @body);";
        index.Parameters.AddWithValue("@row", rowID ?? DBNull.Value);
        index.Parameters.AddWithValue("@title", NormalizeForSearchIndex(article.Title ?? ""));
        index.Parameters.AddWithValue("@body", NormalizeForSearchIndex(body));
        index.ExecuteNonQuery();

        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE articles SET searchRowID = last_insert_rowid() WHERE articleID = @id;";
        update.Parameters.AddWithValue("@id", article.ArticleID);
        update.ExecuteNonQuery();
    }

    private static string NormalizeForSearchIndex(string text)
    {
        var normalized = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            normalized.Append(Rune.IsPunctuation(rune) || Rune.IsSymbol(rune) ? " " : rune.ToString());
        return normalized.ToString().CollapsingWhitespace();
    }
}
