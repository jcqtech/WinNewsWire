using Microsoft.Data.Sqlite;

namespace WinNewsWire.Database;

public static class RSDatabaseInfoTable
{
    public const string TableName = "RSDatabaseInfo";
    public const int DefaultDaysBetweenVacuums = 13;

    public static void CreateTableIfNeeded(SqliteConnection connection)
        => connection.Execute("CREATE TABLE IF NOT EXISTS RSDatabaseInfo (key TEXT PRIMARY KEY NOT NULL, value);");

    public static DateTimeOffset? LastVacuumDate(SqliteConnection connection)
    {
        using var command = connection.Cmd("SELECT value FROM RSDatabaseInfo WHERE key=$key;", ("$key", "lastVacuumDate"));
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        return DateTimeOffset.UnixEpoch.AddSeconds(row.GetDouble(0));
    }

    public static void SetLastVacuumDate(SqliteConnection connection, DateTimeOffset date)
        => connection.Execute("INSERT OR REPLACE INTO RSDatabaseInfo (key, value) VALUES ($key, $value);",
            ("$key", "lastVacuumDate"), ("$value", (date - DateTimeOffset.UnixEpoch).TotalSeconds));
}
