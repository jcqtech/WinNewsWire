namespace WinNewsWire.ArticlesDatabase;

internal static class Constants
{
    public const string TableArticles = "articles";
    public const string TableAuthors = "authors";
    public const string TableAuthorsLookup = "authorsLookup";
    public const string TableAttachments = "attachments";
    public const string TableAttachmentsLookup = "attachmentsLookup";
    public const string TableStatuses = "statuses";
    public const string TableSearch = "search";

    public const string CreateStatements = """
    CREATE TABLE IF NOT EXISTS articles (articleID TEXT NOT NULL PRIMARY KEY, feedID TEXT NOT NULL, uniqueID TEXT NOT NULL, title TEXT, contentHTML TEXT, contentText TEXT, markdown TEXT, url TEXT, externalURL TEXT, summary TEXT, imageURL TEXT, bannerImageURL TEXT, datePublished INTEGER, dateModified INTEGER, searchRowID INTEGER);
    CREATE TABLE IF NOT EXISTS statuses (articleID TEXT NOT NULL PRIMARY KEY, read INTEGER NOT NULL DEFAULT 0, starred INTEGER NOT NULL DEFAULT 0, dateArrived INTEGER NOT NULL DEFAULT 0);
    CREATE TABLE IF NOT EXISTS authors (authorID TEXT NOT NULL PRIMARY KEY, name TEXT, url TEXT, avatarURL TEXT, emailAddress TEXT);
    CREATE TABLE IF NOT EXISTS authorsLookup (authorID TEXT NOT NULL, articleID TEXT NOT NULL, PRIMARY KEY(authorID, articleID));
    CREATE TABLE IF NOT EXISTS attachments (attachmentID TEXT NOT NULL PRIMARY KEY, url TEXT, mimeType TEXT, title TEXT, sizeInBytes INTEGER, durationInSeconds INTEGER);
    CREATE TABLE IF NOT EXISTS attachmentsLookup (attachmentID TEXT NOT NULL, articleID TEXT NOT NULL, PRIMARY KEY(attachmentID, articleID));
    CREATE INDEX IF NOT EXISTS articles_feedID_datePublished_articleID ON articles (feedID, datePublished, articleID);
    CREATE INDEX IF NOT EXISTS statuses_starred_index ON statuses (starred);
    CREATE VIRTUAL TABLE IF NOT EXISTS search USING fts4(title, body);
    CREATE TRIGGER IF NOT EXISTS articles_after_delete_trigger_delete_search_text AFTER DELETE ON articles BEGIN DELETE FROM search WHERE rowid = OLD.searchRowID; END;
    """;
}
