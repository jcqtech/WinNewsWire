using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinNewsWire.AppShared.Scripting;

public enum ScriptingObjectKind { Application, Account, Folder, Feed, Article, Author }

public sealed record ScriptingReference(ScriptingObjectKind Kind = ScriptingObjectKind.Application,
    string? AccountID = null, int? FolderID = null, string? FeedID = null,
    string? ArticleID = null, string? AuthorID = null);

public sealed record ScriptingRequest(string Command, ScriptingReference? Target = null, string? Property = null,
    JsonElement? Value = null, ScriptingObjectKind? NewKind = null, string? Name = null, string? Url = null, string? ID = null);

public sealed record ScriptingError(string Code, string Message);
public sealed record ScriptingResponse(string? ID, bool Success, bool HasValue, JsonElement? Value, ScriptingError? Error);

public static class ScriptingProtocol
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24,
        Converters = { new JsonStringEnumConverter<ScriptingObjectKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static ScriptingRequest Parse(string json)
    {
        var request = JsonSerializer.Deserialize<ScriptingRequest>(json, Options)
            ?? throw new JsonException("An automation request must be an object.");
        if (string.IsNullOrWhiteSpace(request.Command)) throw new ArgumentException("An automation command is required.");
        if (request.ID?.Length > 128) throw new ArgumentException("The request ID is too long.");
        return request;
    }

    public static ScriptingResponse Result(string? id, object? value)
        => new(id, true, true, JsonSerializer.SerializeToElement(value, Options), null);
    public static ScriptingResponse NoResult(string? id) => new(id, true, false, null, null);
    public static ScriptingResponse Failure(string? id, string code, string message)
        => new(id, false, false, null, new(code, message));
}
