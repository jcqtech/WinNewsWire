using Microsoft.Data.Sqlite;

namespace WinNewsWire.Account;

public sealed class AccountSettingsDatabase
{
    private readonly Dictionary<string, (string? Username, string? EndpointUrl)> _credentials = new(StringComparer.Ordinal);

    public AccountSettingsDatabase(string databasePath)
    {
        if (!File.Exists(databasePath)) return;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT accountID, username, endpointURL FROM accountSettings;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            _credentials[reader.GetString(0)] = (reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }
    }

    public string? Username(string accountID) => _credentials.GetValueOrDefault(accountID).Username;
    public string? EndpointUrl(string accountID) => _credentials.GetValueOrDefault(accountID).EndpointUrl;
}
