using Microsoft.Data.Sqlite;

namespace WinNewsWire.Images;

internal sealed record HomePageFaviconRecord(string HomePageUrl, string? FaviconUrl, DateTimeOffset LastChecked);

internal static class HomePageFaviconTable
{
    internal static IReadOnlyList<HomePageFaviconRecord> FetchAll(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT homePageURL, faviconURL, lastChecked FROM homePageFavicon;";
        using var rows = command.ExecuteReader();
        var result = new List<HomePageFaviconRecord>();
        while (rows.Read())
            result.Add(new(rows.GetString(0), rows.IsDBNull(1) ? null : rows.GetString(1), DateTimeOffset.UnixEpoch.AddSeconds(rows.GetDouble(2))));
        return result;
    }

    internal static void Save(SqliteConnection connection, HomePageFaviconRecord row)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO homePageFavicon (homePageURL,faviconURL,lastChecked) VALUES ($home,$icon,$checked);";
        command.Parameters.AddWithValue("$home", row.HomePageUrl);
        command.Parameters.AddWithValue("$icon", (object?)row.FaviconUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$checked", (row.LastChecked - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.ExecuteNonQuery();
    }
}
