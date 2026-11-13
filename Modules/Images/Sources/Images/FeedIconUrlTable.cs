using Microsoft.Data.Sqlite;

namespace WinNewsWire.Images;

internal static class FeedIconUrlTable
{
    internal static IReadOnlyDictionary<string, string> FetchAll(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT feedURL, iconURL FROM feedIconURL;";
        using var rows = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (rows.Read()) result.Add(rows.GetString(0), rows.GetString(1));
        return result;
    }

    internal static void Save(SqliteConnection connection, string feedUrl, string iconUrl)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO feedIconURL (feedURL,iconURL) VALUES ($feed,$icon);";
        command.Parameters.AddWithValue("$feed", feedUrl);
        command.Parameters.AddWithValue("$icon", iconUrl);
        command.ExecuteNonQuery();
    }
}
