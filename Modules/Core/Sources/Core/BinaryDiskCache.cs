namespace WinNewsWire.Core;

/// <summary>Port of BinaryDiskCache. The caller supplies a filename key, not a URL.</summary>
public sealed class BinaryDiskCache
{
    private readonly string _folder;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BinaryDiskCache(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = Path.GetFullPath(folder);
    }

    public BinaryDiskCache()
        : this(Path.Combine(AppConfig.CachesDirectory, "BinaryCache"))
    {
    }

    public string Folder => _folder;

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        var path = PathForKey(key);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try { return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(string key, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var path = PathForKey(key);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_folder);
            await FileUtilities.WriteAllBytesAtomicallyAsync(path, data, ct).ConfigureAwait(false);
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
            try { File.Delete(path); }
            catch (DirectoryNotFoundException) { }
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

    public string PathForKey(string key)
    {
        if (!FileUtilities.IsValidWindowsFileName(key))
            throw new ArgumentException("A binary cache key must be a single valid Windows filename.", nameof(key));
        return Path.Combine(_folder, key);
    }
}
