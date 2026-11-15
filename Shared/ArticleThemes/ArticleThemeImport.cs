using System.Diagnostics;

namespace WinNewsWire.AppShared.ArticleThemes;

public sealed class ArticleThemeImport : IDisposable, IAsyncDisposable
{
    private bool _disposed;
    internal bool Installed { get; set; }
    internal string TemporaryRoot { get; }
    internal ArticleThemesManager Manager { get; }
    public ArticleTheme Theme { get; }
    public bool ReplacesExisting { get; }

    internal ArticleThemeImport(ArticleThemesManager manager, string root, ArticleTheme theme, bool replacesExisting)
    {
        Manager = manager;
        TemporaryRoot = root;
        Theme = theme;
        ReplacesExisting = replacesExisting;
    }

    public ArticleTheme Install(bool overwriteExisting = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Installed) throw new InvalidOperationException("This theme has already been installed.");
        return Manager.InstallPreparedTheme(this, overwriteExisting);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (Directory.Exists(TemporaryRoot)) Directory.Delete(TemporaryRoot, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"Could not clean up theme import directory {TemporaryRoot}: {error.Message}");
        }
    }

    public ValueTask DisposeAsync() => new(Task.Run(Dispose));
}
