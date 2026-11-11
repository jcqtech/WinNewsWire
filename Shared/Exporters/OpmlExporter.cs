namespace WinNewsWire.AppShared.Exporters;

/// <summary>Port of <c>OPMLExporter</c>.</summary>
public static class OpmlExporter
{
    public static string OPMLString(Account.Account account, string title)
        => account.ExportOpml(title);

    public static Task ExportAsync(Account.Account account, string path)
        => File.WriteAllTextAsync(path, OPMLString(account, account.Name));
}
