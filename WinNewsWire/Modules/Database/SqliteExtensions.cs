using System.Data;
using Microsoft.Data.Sqlite;

namespace WinNewsWire.Database;

/// <summary>Port of <c>FMDatabase+Extras</c> / <c>FMResultSet+Extras</c>.</summary>
public static class SqliteExtensions
{
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

    public static string? GetStringOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? GetInt64OrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static int? GetInt32OrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static bool GetBool(this SqliteDataReader r, int i) => !r.IsDBNull(i) && r.GetInt32(i) != 0;

    public static DateTime? GetUnixTimeOrNull(this SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        return DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(i)).UtcDateTime;
    }

    public static long? ToUnixSeconds(this DateTime? d)
        => d is null ? null : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();

    public static long ToUnixSeconds(this DateTime d)
        => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
