using System.Text.Json;
using System.Text;
using WinNewsWire.Core;

namespace WinNewsWire.Account.OneDrive;

internal sealed record OneDriveRegister(long Clock, string Device, string? Value);

internal sealed class OneDriveDocument
{
    public int Version { get; set; } = 1;
    public Dictionary<string, OneDriveRegister> Registers { get; set; } = new(StringComparer.Ordinal);

    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException($"Unsupported OneDrive sync format {Version}. Upgrade WinNewsWire before syncing.");
        if (Registers is null || Registers.Any(p => string.IsNullOrEmpty(p.Key) || p.Value is null
            || p.Value.Clock < 1 || !Guid.TryParseExact(p.Value.Device, "N", out _)))
            throw new InvalidDataException("Invalid OneDrive synchronization document.");
        foreach (var (key, register) in Registers)
        {
            var parts = key.Split('/');
            bool valid = parts switch
            {
                ["folder", var id] => Guid.TryParseExact(id, "N", out _) && (register.Value is null || !string.IsNullOrWhiteSpace(register.Value)),
                ["feed", var id] => IsFeedUrl(Decode(id)),
                ["title" or "homepage", var id] => IsFeedUrl(Decode(id)),
                ["member", var id, var folder] => IsFeedUrl(Decode(id))
                    && (folder == "root" || Guid.TryParseExact(folder, "N", out _)) && register.Value is null or "1",
                ["status", "Read" or "Starred" or "Deleted", var id] => !string.IsNullOrEmpty(Decode(id)) && register.Value is "0" or "1",
                ["content", var id] => ValidContent(Decode(id), register.Value),
                ["preference", "unreadContent"] => register.Value is "0" or "1",
                _ => false
            };
            if (!valid) throw new InvalidDataException($"Invalid OneDrive synchronization record: {key}");
        }
    }

    internal static string Decode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
    }

    private static bool IsFeedUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    private static bool ValidContent(string id, string? value)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (value is null) return true;
        var article = JsonSerializer.Deserialize<OneDriveArticle>(value);
        if (article is null || article.ID != id || string.IsNullOrEmpty(article.UniqueID) || !IsFeedUrl(article.FeedUrl)) return false;
        if (article.Authors is not null && Articles.Author.FromJson(article.Authors) is null) return false;
        _ = Articles.Attachment.FromJson(article.Attachments);
        return true;
    }
}

internal sealed class OneDriveReplica
{
    internal sealed class State
    {
        public string Device { get; set; } = Guid.NewGuid().ToString("N");
        public long Clock { get; set; }
        public OneDriveDocument Document { get; set; } = new();
        public Dictionary<string, string?> Structure { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> AppliedContent { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> AppliedStatuses { get; set; } = new(StringComparer.Ordinal);
        public bool? UnreadContentPreference { get; set; }
        public bool? PendingPreference { get; set; }
    }

    private readonly string _path;
    private bool _dirty;
    internal State Data { get; }
    internal OneDriveReplica(string path)
    {
        _path = path;
        var exists = File.Exists(path);
        Data = exists ? JsonSerializer.Deserialize<State>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty local OneDrive replica.") : new();
        _dirty = !exists;
        if (!Guid.TryParseExact(Data.Device, "N", out _) || Data.Document is null || Data.Structure is null
            || Data.AppliedContent is null || Data.AppliedStatuses is null || Data.Clock < 0)
            throw new InvalidDataException("Invalid local OneDrive replica.");
        Data.Document.Validate();
        Data.Clock = Math.Max(Data.Clock, Data.Document.Registers.Values.Select(r => r.Clock).DefaultIfEmpty().Max());
    }

    internal void Set(string key, string? value)
    {
        Data.Document.Registers[key] = new(checked(++Data.Clock), Data.Device, value);
        _dirty = true;
    }

    internal bool Merge(OneDriveDocument remote)
    {
        remote.Validate();
        foreach (var (key, incoming) in remote.Registers)
            if (Data.Document.Registers.TryGetValue(key, out var current)
                && incoming.Clock == current.Clock && incoming.Device == current.Device && incoming.Value != current.Value)
                throw new InvalidDataException("Conflicting OneDrive records have the same revision.");
        bool changed = false;
        foreach (var (key, incoming) in remote.Registers)
        {
            if (incoming.Clock > Data.Clock) { Data.Clock = incoming.Clock; _dirty = true; }
            if (!Data.Document.Registers.TryGetValue(key, out var current)
                || incoming.Clock > current.Clock
                || (incoming.Clock == current.Clock && string.CompareOrdinal(incoming.Device, current.Device) > 0))
            {
                Data.Document.Registers[key] = incoming;
                changed = true;
                _dirty = true;
            }
        }
        return changed;
    }

    internal void CaptureStructure(Dictionary<string, string?> current)
    {
        foreach (var key in current.Keys.Union(Data.Structure.Keys).ToArray())
        {
            current.TryGetValue(key, out var value);
            if (!Data.Structure.TryGetValue(key, out var previous) || previous != value)
                Set(key, value);
        }
        RememberStructure(current);
        Save();
    }

    internal void RememberStructure(Dictionary<string, string?> structure)
    {
        if (Data.Structure.Count == structure.Count && Data.Structure.All(p => structure.TryGetValue(p.Key, out var value) && p.Value == value)) return;
        Data.Structure = structure;
        _dirty = true;
    }

    internal bool ContentWasApplied(string key, OneDriveRegister value)
        => Data.AppliedContent.GetValueOrDefault(key) == $"{value.Clock}:{value.Device}";

    internal void RememberContent(string key, OneDriveRegister value)
    {
        if (ContentWasApplied(key, value)) return;
        Data.AppliedContent[key] = $"{value.Clock}:{value.Device}";
        _dirty = true;
    }

    internal bool StatusWasApplied(string key, OneDriveRegister value)
        => Data.AppliedStatuses.GetValueOrDefault(key) == $"{value.Clock}:{value.Device}";

    internal void RememberStatus(string key, OneDriveRegister value)
    {
        if (StatusWasApplied(key, value)) return;
        Data.AppliedStatuses[key] = $"{value.Clock}:{value.Device}";
        _dirty = true;
    }

    internal void Save()
    {
        if (!_dirty) return;
        FileUtilities.WriteAllTextAtomically(_path, JsonSerializer.Serialize(Data));
        _dirty = false;
    }

    internal void RememberPreference(bool value, bool pending)
    {
        Data.UnreadContentPreference = value;
        Data.PendingPreference = pending ? value : null;
        _dirty = true;
    }
}
