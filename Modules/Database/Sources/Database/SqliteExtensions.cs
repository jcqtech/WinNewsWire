using System.Data;
using Microsoft.Data.Sqlite;

namespace WinNewsWire.Database;

/// <summary>Port of <c>FMDatabase+Extras</c> / <c>FMResultSet+Extras</c>.</summary>
public static class SqliteExtensions
{
    public static void SetUpDatabase(this SqliteConnection connection)
    {
        var mode = connection.Scalar<string>("PRAGMA journal_mode=DELETE;");
        if (mode != "delete" && !(mode == "memory"
            && string.IsNullOrEmpty(connection.Scalar<string>("SELECT file FROM pragma_database_list WHERE name='main';"))))
            throw new IOException($"SQLite could not use DELETE journaling for {connection.DataSource}; it returned {mode ?? "no mode"}.");
        connection.Execute("PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
    }

    public static SqliteCommand Cmd(this SqliteConnection c, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    public static SqliteCommand Cmd(this SqliteConnection c, string sql, params (string name, object? value)[] parameters)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public static int Execute(this SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.Cmd(sql, p);
        return cmd.ExecuteNonQuery();
    }

    public static T? Scalar<T>(this SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.Cmd(sql, p);
        var o = cmd.ExecuteScalar();
        if (o is null || o is DBNull) return default;
        return (T)Convert.ChangeType(o, typeof(T))!;
    }

    public static List<T> Query<T>(this SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] p)
    {
        using var cmd = c.Cmd(sql, p);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static void VacuumIfNeeded(this SqliteConnection connection, TimeProvider? clock = null,
        int daysBetweenVacuums = RSDatabaseInfoTable.DefaultDaysBetweenVacuums)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(daysBetweenVacuums);
        clock ??= TimeProvider.System;
        RSDatabaseInfoTable.CreateTableIfNeeded(connection);
        if (RSDatabaseInfoTable.LastVacuumDate(connection) is { } lastVacuum
            && clock.GetUtcNow() - lastVacuum < TimeSpan.FromDays(daysBetweenVacuums)) return;
        connection.Execute("VACUUM;");
        RSDatabaseInfoTable.SetLastVacuumDate(connection, clock.GetUtcNow());
    }

    public static string? GetStringOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? GetInt64OrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static int? GetInt32OrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static bool GetBool(this SqliteDataReader r, int i) => !r.IsDBNull(i) && r.GetInt32(i) != 0;

    public static DateTime? GetUnixTimeOrNull(this SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        return r.GetUnixTime(i);
    }

    public static DateTime GetUnixTime(this SqliteDataReader r, int i)
        => DateTime.UnixEpoch.AddSeconds(r.GetDouble(i));

    public static double ToUnixTimestamp(this DateTime date)
        => (DateTime.SpecifyKind(date, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;

    public static long? ToUnixSeconds(this DateTime? d)
        => d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();

    public static long ToUnixSeconds(this DateTime d)
        => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
