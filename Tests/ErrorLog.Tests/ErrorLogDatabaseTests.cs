using Microsoft.Data.Sqlite;
using WinNewsWire.ErrorLog;
using Xunit;

namespace ErrorLog.Tests;

public sealed class ErrorLogDatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ErrorLogDatabase _db;

    public ErrorLogDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"errorlog_test_{Guid.NewGuid():N}.sqlite");
        _db = new ErrorLogDatabase(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static ErrorLogEntry MakeEntry(
        string sourceName = "TestAccount",
        int sourceID = 1,
        string operation = "refresh",
        string message = "Something went wrong",
        DateTime? date = null) =>
        new(0, date ?? DateTime.UtcNow, sourceName, sourceID, operation, "TestFile.cs", "TestMethod", 42, message);

    [Fact]
    public async Task Log_and_fetch_single_entry()
    {
        var entry = MakeEntry();
        await _db.AddAsync(entry);

        var results = await _db.FetchRecentAsync();

        Assert.Single(results);
        Assert.Equal(entry.ErrorMessage, results[0].ErrorMessage);
    }

    [Fact]
    public async Task Log_multiple_entries_and_fetch_all()
    {
        await _db.AddAsync(MakeEntry(message: "Error 1"));
        await _db.AddAsync(MakeEntry(message: "Error 2"));
        await _db.AddAsync(MakeEntry(message: "Error 3"));

        var results = await _db.FetchRecentAsync();

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task Clear_removes_all_entries()
    {
        await _db.AddAsync(MakeEntry(message: "Error 1"));
        await _db.AddAsync(MakeEntry(message: "Error 2"));

        await _db.ClearAsync();

        var results = await _db.FetchRecentAsync();
        Assert.Empty(results);
    }

    [Fact]
    public async Task FetchRecent_on_empty_database_returns_empty_list()
    {
        var results = await _db.FetchRecentAsync();
        Assert.Empty(results);
    }

    [Fact]
    public async Task EntryAdded_event_fires_when_logging()
    {
        ErrorLogEntry? received = null;
        _db.EntryAdded += (_, e) => received = e;

        var entry = MakeEntry(message: "event test");
        await _db.AddAsync(entry);

        Assert.NotNull(received);
        Assert.Equal("event test", received.ErrorMessage);
    }

    [Fact]
    public async Task Entry_fields_round_trip_correctly()
    {
        var date = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var entry = MakeEntry(
            sourceName: "MyAccount",
            sourceID: 99,
            operation: "sync",
            message: "Round-trip test",
            date: date);

        await _db.AddAsync(entry);
        var results = await _db.FetchRecentAsync();

        var fetched = Assert.Single(results);
        Assert.True(fetched.Id > 0);
        Assert.Equal(date, fetched.Date);
        Assert.Equal("MyAccount", fetched.SourceName);
        Assert.Equal(99, fetched.SourceID);
        Assert.Equal("sync", fetched.Operation);
        Assert.Equal("TestFile.cs", fetched.FileName);
        Assert.Equal("TestMethod", fetched.FunctionName);
        Assert.Equal(42, fetched.LineNumber);
        Assert.Equal("Round-trip test", fetched.ErrorMessage);
    }
}
