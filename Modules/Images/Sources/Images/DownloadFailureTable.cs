using Microsoft.Data.Sqlite;

namespace WinNewsWire.Images;

internal static class DownloadFailureTable
{
    internal static IReadOnlyDictionary<string, DateTimeOffset> FetchAll(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT url, lastChecked FROM downloadFailure;";
        using var rows = command.ExecuteReader();
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        while (rows.Read()) result.Add(rows.GetString(0), DateTimeOffset.UnixEpoch.AddSeconds(rows.GetDouble(1)));
        return result;
    }

    internal static void Save(SqliteConnection connection, string url, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO downloadFailure (url,lastChecked) VALUES ($url,$checked);";
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$checked", (now - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.ExecuteNonQuery();
    }

    internal static void Clear(SqliteConnection connection, string url)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM downloadFailure WHERE url=$url;";
        command.Parameters.AddWithValue("$url", url);
        command.ExecuteNonQuery();
    }

    internal static void RemoveExpired(SqliteConnection connection, DateTimeOffset cutoff)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM downloadFailure WHERE lastChecked < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", (cutoff - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.ExecuteNonQuery();
    }
}
