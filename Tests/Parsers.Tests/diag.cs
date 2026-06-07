using WinNewsWire.Parsers;
using System.IO;
var path = @"Resources\theomnishow.rss";
var data = File.ReadAllBytes(path);
var feed = FeedParser.Parse(new ParserData("https://theomnishow.omnigroup.com/", data))!;
Console.WriteLine($"items: {feed.Items.Count}");
int idx = 0;
foreach (var i in feed.Items)
{
    var atts = i.Attachments;
    Console.WriteLine($"[{idx++}] title={i.Title?.Substring(0, Math.Min(30, i.Title.Length))} atts={(atts?.Count ?? 0)}");
    if (atts != null) foreach (var a in atts) Console.WriteLine($"    {a.Url}");
}
