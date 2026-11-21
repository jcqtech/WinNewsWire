namespace WinNewsWire.Account.OneDrive;

public static class OneDriveLocations
{
    public static IReadOnlyList<string> FindRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } path && Directory.Exists(path))
                roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        if (OperatingSystem.IsWindows()) AddRegisteredRoots(roots);
        return roots.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddRegisteredRoots(HashSet<string> roots)
    {
        using var accounts = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        if (accounts is null) return;
        foreach (var name in accounts.GetSubKeyNames())
        {
            using var account = accounts.OpenSubKey(name);
            if (account?.GetValue("UserFolder") is string path && Directory.Exists(path))
                roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        }
    }

    public static string ValidateSyncDirectory(string directory, IEnumerable<string> roots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!roots.Any(root => path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Choose a dedicated folder inside a OneDrive account configured on this PC, not the OneDrive root.");
        if (path.Length > 220) throw new ArgumentException("Choose a shorter OneDrive folder path (220 characters or fewer).");
        return path;
    }
}
