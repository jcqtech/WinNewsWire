namespace WinNewsWire.Web;

public static class BrowserPathDisplay
{
    public static string ForCanonicalParentPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length >= 2 && components[0] == "Volumes")
            {
                var inside = components.Skip(2).ToArray();
                return "/" + components[1] + (inside.Length == 0 ? ""
                    : inside.Length <= 3 ? "/" + string.Join("/", inside) : "/\u2026/" + inside[^1]);
            }
            return components.Length <= 3 ? path : "/" + components[0] + "/\u2026/" + components[^1];
        }

        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return parts.Length <= 5 ? path : @"\\" + parts[0] + @"\" + parts[1] + "\\\u2026\\" + parts[^1];
        return parts.Length <= 4 ? path : parts[0] + @"\" + parts[1] + "\\\u2026\\" + parts[^1];
    }
}
