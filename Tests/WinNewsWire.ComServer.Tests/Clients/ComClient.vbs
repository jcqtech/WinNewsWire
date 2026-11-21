Option Explicit
Dim app, account, feed, article, item, count, folder, properties
Set app = CreateObject(WScript.Arguments(0))
If app.Name <> "WinNewsWire" Then Err.Raise 1, , "Name"
Set account = app.Accounts.Item(1)
count = 0
For Each item In account.AllFeeds
    count = count + 1
Next
If count <> 2 Then Err.Raise 1, , "Enumeration"
Set feed = account.Feeds.Item("Fixture Feed")
Set article = feed.Articles.ByID("publisher-id")
If VarType(article.PublishedDate) <> vbDate Then Err.Raise 1, , "Date"
article.Read = True
article.Starred = True
If Not article.Read Or Not article.Starred Then Err.Raise 1, , "Writes"
Set properties = CreateObject("Scripting.Dictionary")
properties.Add "name", "VBScript folder"
Set folder = app.Make("folder", account, , properties)
If folder.Name <> "VBScript folder" Then Err.Raise 1, , "Make"
app.Delete folder
If app.Exists(folder) Then Err.Raise 1, , "Delete"
WScript.Echo "COM PARITY OK"
