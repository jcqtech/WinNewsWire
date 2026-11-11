using System.Diagnostics;
using System.Text;

namespace WinNewsWire.Core;

public static class FileUtilities
{
    public static bool IsValidWindowsFileName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.EndsWith(' ') || name.EndsWith('.')
            || name.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character))) return false;
        var stem = name.Split('.')[0];
        return !System.Text.RegularExpressions.Regex.IsMatch(stem,
            @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9\u00B9\u00B2\u00B3]|LPT[1-9\u00B9\u00B2\u00B3])$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    public static void WriteAllTextAtomically(string path, string contents)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            RemoveTemporaryFile(temporaryPath);
        }
    }

    public static async Task WriteAllBytesAtomicallyAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, contents, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { RemoveTemporaryFile(temporaryPath); }
    }

    public static void WriteAllBytesAtomically(string path, byte[] contents)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { RemoveTemporaryFile(temporaryPath); }
    }

    private static void RemoveTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException ex) { Trace.TraceError($"Could not remove temporary file {path}: {ex}"); }
        catch (UnauthorizedAccessException ex) { Trace.TraceError($"Could not remove temporary file {path}: {ex}"); }
    }
}
