using Microsoft.Data.Sqlite;
using WinNewsWire.Database;
using Xunit;

namespace WinNewsWire.ArticlesDatabase.Tests;

public sealed class DatabaseInitializationTests
{
    [Fact]
    public void FailedSchemaSetupReleasesItsConnectionAndKeepsExistingData()
    {
        string path = Path.Combine(Path.GetTempPath(), "article-setup-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var seed = Open(path))
                seed.Execute("CREATE TABLE articles (original TEXT); INSERT INTO articles VALUES ('keep');");
            Assert.Throws<SqliteException>(() => new ArticlesDatabaseStore(path, "fixture"));
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.True(exclusive.Length > 0);
            using var reopened = Open(path);
            Assert.Equal("keep", reopened.Scalar<string>("SELECT original FROM articles;"));
        }
        finally { File.Delete(path); }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
