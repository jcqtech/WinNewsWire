using WinNewsWire.AppShared.AccountStats;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class AccountStatsTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DatabaseSizeIncludesOnlyAccountDatabasesAndTheirExistingWalSidecars(bool primaryExists, bool sidecarsExist)
    {
        string directory = Path.Combine(Path.GetTempPath(), "account-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            long expected = 0;
            foreach (var name in new[] { "DB.sqlite3", "FeedSettings.db", "Sync.sqlite3" })
            {
                if (primaryExists)
                {
                    File.WriteAllBytes(Path.Combine(directory, name), new byte[7]);
                    expected += 7;
                }
                if (sidecarsExist)
                {
                    File.WriteAllBytes(Path.Combine(directory, name + "-wal"), new byte[11]);
                    File.WriteAllBytes(Path.Combine(directory, name + "-shm"), new byte[13]);
                    expected += 24;
                }
            }
            File.WriteAllBytes(Path.Combine(directory, "DB.sqlite3-journal"), new byte[29]);
            File.WriteAllBytes(Path.Combine(directory, "Images.db-wal"), new byte[31]);
            Assert.Equal(expected, AccountStatsViewModel.DatabaseSize(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
