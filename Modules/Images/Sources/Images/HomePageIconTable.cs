using Microsoft.Data.Sqlite;

namespace WinNewsWire.Images;

internal sealed record HomePageIconRecord(string HomePageUrl, string? IconUrl, DateTimeOffset LastChecked);

internal static class HomePageIconTable
{
    internal static IReadOnlyList<HomePageIconRecord> FetchAll(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT homePageURL, iconURL, lastChecked FROM homePageIcon;";
        using var rows = command.ExecuteReader();
        var result = new List<HomePageIconRecord>();
        while (rows.Read())
            result.Add(new(rows.GetString(0), rows.IsDBNull(1) ? null : rows.GetString(1), DateTimeOffset.UnixEpoch.AddSeconds(rows.GetDouble(2))));
        return result;
    }

    internal static void Save(SqliteConnection connection, HomePageIconRecord row)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO homePageIcon (homePageURL,iconURL,lastChecked) VALUES ($home,$icon,$checked);";
        command.Parameters.AddWithValue("$home", row.HomePageUrl);
        command.Parameters.AddWithValue("$icon", (object?)row.IconUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$checked", (row.LastChecked - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.ExecuteNonQuery();
    }
}
