using System.Security.Cryptography;
using System.Text;

namespace WinNewsWire.Core;

/// <summary>
/// Filesystem-backed binary cache keyed by string (typically URLs).
/// Keys are hashed to safe filenames via SHA256.
/// </summary>
public sealed class BinaryDiskCache
{
    private readonly string _folder;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _directoryCreated;

    public BinaryDiskCache(string folder)
    {
        _folder = folder;
    }

    public BinaryDiskCache()
        : this(Path.Combine(AppConfig.CachesDirectory, "BinaryCache"))
    {
    }

    public string Folder => _folder;

    public async Task<byte[]?> GetAsync(string key)
    {
        var path = PathForKey(key);
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
                return null;
            return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(string key, byte[] data)
    {
        EnsureDirectory();
        var path = PathForKey(key);
        var tempPath = path + ".tmp";
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(tempPath, data).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Remove(string key)
    {
        var path = PathForKey(key);
        _lock.Wait();
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void RemoveAll()
    {
        _lock.Wait();
        try
        {
            if (Directory.Exists(_folder))
            {
                foreach (var file in Directory.EnumerateFiles(_folder))
                    File.Delete(file);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void EnsureDirectory()
    {
        if (!_directoryCreated)
        {
            Directory.CreateDirectory(_folder);
            _directoryCreated = true;
        }
    }

    private string PathForKey(string key)
    {
        return Path.Combine(_folder, HashKey(key));
    }

    private static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
