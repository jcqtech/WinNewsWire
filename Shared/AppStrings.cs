using System.Globalization;
using System.Resources;

namespace WinNewsWire.AppShared;

public static class AppStrings
{
    private static readonly ResourceManager Resources = new("WinNewsWire.AppShared.Resources.Strings", typeof(AppStrings).Assembly);

    public static string Get(string key)
        => Resources.GetString(key, CultureInfo.CurrentUICulture)
            ?? throw new MissingManifestResourceException($"The application string '{key}' is missing.");
}
