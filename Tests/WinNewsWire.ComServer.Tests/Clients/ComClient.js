var app = new ActiveXObject(WScript.Arguments(0));
function check(value, message) { if (!value) throw new Error(message); }
check(app.Name === "WinNewsWire", "Name");
var account = app.Accounts.Item(1);
var count = 0;
for (var e = new Enumerator(account.AllFeeds); !e.atEnd(); e.moveNext()) count++;
check(count === 2, "Enumeration");
var feed = account.Feeds.Item("Fixture Feed");
var article = feed.Articles.ByID("publisher-id");
check(article.ID === "publisher-id", "Article ID");
article.Read = true;
check(article.Read, "Write");
var folder = account.CreateFolder("JScript folder");
var nested = folder.CreateFeed("https://example.test/jscript", "JScript feed");
check(folder.Feeds.Count === 1, "Create");
nested.Delete();
folder.Delete();
check(!app.Exists(folder), "Exists");
WScript.Echo("COM PARITY OK");
