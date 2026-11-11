using Claunia.PropertyList;

namespace WinNewsWire.Account.Tests;

internal sealed class SettingsTestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinNewsWire.SettingsTests." + Guid.NewGuid().ToString("N"));

    public SettingsTestDirectory() => Directory.CreateDirectory(Path);

    public void WritePlist(string filename, Dictionary<string, object> values, bool binary = false)
    {
        var root = NSObject.Wrap(values);
        var file = new FileInfo(System.IO.Path.Combine(Path, filename));
        if (binary) PropertyListParser.SaveAsBinary(root, file);
        else PropertyListParser.SaveAsXml(root, file);
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
