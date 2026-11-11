using System.Text;
using System.Xml.Linq;

namespace WinNewsWire.AppShared.Importers;

/// <summary>
/// Port of <c>Mac/MainWindow/NNW3/NNW3Document.swift</c> — converts a NetNewsWire 3 subscriptions
/// export (Apple XML property list) into OPML, which can then be fed to the normal OPML
/// importer. Binary plists are not supported by this lightweight implementation — if the
/// file begins with <c>bplist</c> the caller receives a null result and should prompt the
/// user to convert the file with <c>plutil -convert xml1</c> first.
/// </summary>
public static class Nnw3Importer
{
    public static async Task<string?> ConvertToOpmlAsync(string subscriptionsPlistPath, CancellationToken ct = default)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(subscriptionsPlistPath, ct);
            if (bytes.Length >= 6 && Encoding.ASCII.GetString(bytes, 0, 6) == "bplist")
                return null; // binary plist — unsupported
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Ignore,
                XmlResolver = null,
            };
            using var ms = new MemoryStream(bytes);
            using var xr = System.Xml.XmlReader.Create(ms, settings);
            var doc = XDocument.Load(xr);
            var root = doc.Root?.Element("array");
            if (root is null) return null;

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<opml version=\"1.1\"><head><title>NetNewsWire 3 Subscriptions</title></head><body>");
            foreach (var item in root.Elements("dict"))
                AppendItem(sb, item, 1);
            sb.AppendLine("</body></opml>");
            return sb.ToString();
        }
        catch { return null; }
    }

    private static void AppendItem(StringBuilder sb, XElement dict, int indent)
    {
        var map = ParseDict(dict);
        bool isContainer = map.TryGetValue("isContainer", out var ic) && ic == "true";
        string pad = new('\t', indent);
        if (isContainer)
        {
            var name = Esc(map.GetValueOrDefault("name", string.Empty));
            sb.Append(pad).AppendLine($"<outline text=\"{name}\" title=\"{name}\">");
            if (map.TryGetValue("childrenArray", out _) && map.TryGetValue("__childrenArray__", out _))
            {
                // handled below via raw children collection
            }
            foreach (var child in ChildrenArray(dict))
                AppendItem(sb, child, indent + 1);
            sb.Append(pad).AppendLine("</outline>");
        }
        else
        {
            var t = Esc(map.GetValueOrDefault("name", string.Empty));
            var h = Esc(map.GetValueOrDefault("home", string.Empty));
            var f = Esc(map.GetValueOrDefault("rss", string.Empty));
            sb.Append(pad).AppendLine(
                $"<outline text=\"{t}\" title=\"{t}\" description=\"\" type=\"rss\" version=\"RSS\" htmlUrl=\"{h}\" xmlUrl=\"{f}\"/>");
        }
    }

    private static IEnumerable<XElement> ChildrenArray(XElement dict)
    {
        // Find <key>childrenArray</key><array>…</array>
        var el = dict.Elements().ToList();
        for (int i = 0; i < el.Count - 1; i++)
        {
            if (el[i].Name.LocalName == "key" && el[i].Value == "childrenArray"
                && el[i + 1].Name.LocalName == "array")
            {
                foreach (var child in el[i + 1].Elements("dict")) yield return child;
                yield break;
            }
        }
    }

    private static Dictionary<string, string> ParseDict(XElement dict)
    {
        var map = new Dictionary<string, string>();
        var el = dict.Elements().ToList();
        for (int i = 0; i < el.Count - 1; i++)
        {
            if (el[i].Name.LocalName != "key") continue;
            var key = el[i].Value;
            var valueEl = el[i + 1];
            switch (valueEl.Name.LocalName)
            {
                case "string": map[key] = valueEl.Value; break;
                case "true": map[key] = "true"; break;
                case "false": map[key] = "false"; break;
                case "integer": map[key] = valueEl.Value; break;
            }
        }
        return map;
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? string.Empty;
}
