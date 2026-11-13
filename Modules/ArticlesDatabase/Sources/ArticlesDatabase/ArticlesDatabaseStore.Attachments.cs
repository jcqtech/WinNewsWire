using Microsoft.Data.Sqlite;

namespace WinNewsWire.ArticlesDatabase;

public sealed partial class ArticlesDatabaseStore
{
    private static void MigrateAttachmentStorage(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(articles);";
            using var reader = columns.ExecuteReader();
            while (reader.Read())
                if (reader.GetString(1) == "attachments")
                {
                    transaction.Commit();
                    return;
                }
        }
        using var migrate = connection.CreateCommand();
        migrate.CommandText = """
            ALTER TABLE articles ADD COLUMN attachments TEXT;
            WITH legacy AS (
                SELECT al.articleID, json_group_array(json_object(
                    'attachmentID', a.attachmentID, 'url', a.url, 'mimeType', a.mimeType,
                    'title', a.title, 'sizeInBytes', a.sizeInBytes, 'durationInSeconds', a.durationInSeconds)) AS data
                FROM attachmentsLookup al JOIN attachments a ON al.attachmentID = a.attachmentID
                GROUP BY al.articleID
            )
            UPDATE articles SET attachments = legacy.data FROM legacy WHERE articles.articleID = legacy.articleID;
            """;
        migrate.ExecuteNonQuery();
        transaction.Commit();
    }
}
