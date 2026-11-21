using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinNewsWire.AppShared.Scripting;

namespace WinNewsWire.Automation;

internal interface IAutomationTransport
{
    ScriptingResponse Send(ScriptingRequest request);
}

internal sealed class AutomationSession(IAutomationTransport transport)
{
    private readonly List<WeakReference> _objects = [];

    internal T Track<T>(T value) where T : class
    {
        _objects.Add(new(value));
        return value;
    }

    internal bool HasLiveObjects()
    {
        _objects.RemoveAll(reference => !reference.IsAlive);
        return _objects.Count != 0;
    }

    internal JsonElement? Send(ScriptingRequest request)
    {
        var response = transport.Send(request);
        if (!response.Success)
            throw new COMException(response.Error?.Message ?? "The automation operation failed.", response.Error?.Code switch
            {
                "notFound" => Errors.NotFound,
                "invalidRequest" => Errors.InvalidArgument,
                _ => Errors.OperationFailed
            });
        return response.HasValue ? response.Value : null;
    }

    internal object? Get(ScriptingReference target, string property)
    {
        var canonical = CanonicalProperty(property);
        var result = Send(new("get", target, canonical));
        return result is { } value ? ConvertValue(value, canonical) : null;
    }

    internal object?[] GetMany(ScriptingReference[] targets, string property)
    {
        var canonical = CanonicalProperty(property);
        var values = new List<object?>(targets.Length);
        foreach (var batch in targets.Chunk(128))
        {
            var result = Send(new("getMany", Property: canonical, Targets: batch));
            if (result is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() != batch.Length)
                throw new COMException("The application returned an invalid property projection.", Errors.OperationFailed);
            values.AddRange(array.EnumerateArray().Select(value => ConvertValue(value, canonical)));
        }
        return values.ToArray();
    }

    private static string CanonicalProperty(string property)
        => property?.ToLowerInvariant() switch
        {
            "id" => "id", "name" => "name", "version" => "version", "url" => "url",
            "accounttype" => "accountType", "active" => "active", "opmlrepresentation" => "opmlRepresentation",
            "homepageurl" => "homePageUrl", "iconurl" => "iconUrl", "faviconurl" => "faviconUrl",
            "title" => "title", "externalurl" => "externalUrl", "permalink" => "permalink",
            "contents" => "contents", "html" => "html", "summary" => "summary",
            "publisheddate" or "datepublished" => "publishedDate", "arriveddate" or "datearrived" => "arrivedDate",
            "modifieddate" or "datemodified" => "modifiedDate", "read" => "read", "starred" => "starred",
            "deleted" => "deleted", "imageurl" => "imageUrl", "feed" => "feed",
            "avatarurl" => "avatarUrl", "email" or "emailaddress" => "emailAddress",
            "currentarticle" => "currentArticle",
            _ => throw new COMException("Unknown scalar scripting property: " + property, Errors.InvalidArgument)
        };

    private object? ConvertValue(JsonElement value, string canonical)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (canonical is "publishedDate" or "arrivedDate" or "modifiedDate")
            return value.GetDateTime().ToLocalTime();
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetInt32(),
            JsonValueKind.Object => Wrap(value.Deserialize<ScriptingReference>(ScriptingProtocol.Options)!),
            _ => throw new COMException("The application returned an invalid scalar value.", Errors.OperationFailed)
        };
    }

    internal ScriptObject Wrap(ScriptingReference reference) => reference.Kind switch
    {
        ScriptingObjectKind.Application => Track<ScriptObject>(new ScriptApplication(this)),
        ScriptingObjectKind.Account => Track<ScriptObject>(new ScriptAccount(this, reference)),
        ScriptingObjectKind.Folder => Track<ScriptObject>(new ScriptFolder(this, reference)),
        ScriptingObjectKind.Feed => Track<ScriptObject>(new ScriptFeed(this, reference)),
        ScriptingObjectKind.Article => Track<ScriptObject>(new ScriptArticle(this, reference)),
        ScriptingObjectKind.Author => Track<ScriptObject>(new ScriptAuthor(this, reference)),
        _ => throw new COMException("The application returned an invalid object kind.", Errors.OperationFailed)
    };

    internal ScriptCollection Collection(ScriptingReference target, string property)
        => Track(new ScriptCollection(this, () =>
            Send(new("get", target, property))?.Deserialize<ScriptingReference[]>(ScriptingProtocol.Options)
                ?? throw new COMException("The application returned an invalid collection.", Errors.OperationFailed),
            () => Send(new("count", target, property))!.Value.GetInt32(),
            (offset, limit) => Send(new("get", target, property, Offset: offset, Limit: limit))
                ?.Deserialize<ScriptingReference[]>(ScriptingProtocol.Options)
                ?? throw new COMException("The application returned an invalid collection page.", Errors.OperationFailed)));

    internal ScriptObject RequireObject(object? value)
        => value is ScriptObject item && ReferenceEquals(item.Session, this) ? item
            : throw new COMException("Expected a WinNewsWire object from this application.", Errors.InvalidArgument);

    internal ScriptCollection RequireCollection(object? value)
        => value is ScriptCollection items && ReferenceEquals(items.Session, this) ? items
            : throw new COMException("Expected a WinNewsWire object collection from this application.", Errors.InvalidArgument);

    internal ScriptObject Make(string kind, object? at, object? data, object? properties)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new COMException("The class of the new object is required.", Errors.InvalidArgument);
        var newKind = kind.ToLowerInvariant() switch
        {
            "feed" or "webfeed" => ScriptingObjectKind.Feed,
            "folder" => ScriptingObjectKind.Folder,
            _ => throw new COMException("Only feeds and folders can be created.", Errors.InvalidArgument)
        };
        var target = IsMissing(at) ? new ScriptingReference() : RequireObject(at).Reference;
        var name = Property(properties, "name") ?? (newKind == ScriptingObjectKind.Folder ? "" : null);
        var url = IsMissing(data) ? Property(properties, "url") : data as string
            ?? throw new COMException("With data must be a feed URL string.", Errors.InvalidArgument);
        return Make(newKind, target, name, url);
    }

    internal ScriptObject Make(ScriptingObjectKind kind, ScriptingReference target, string? name, string? url)
    {
        var result = Send(new("make", target, NewKind: kind, Name: name, Url: url));
        return Wrap(result?.Deserialize<ScriptingReference>(ScriptingProtocol.Options)
            ?? throw new COMException("The application did not return the created object.", Errors.OperationFailed));
    }

    private static bool IsMissing(object? value) => value is null or Missing or DBNull;

    private static string? Property(object? properties, string key)
    {
        if (IsMissing(properties)) return null;
        object? value;
        if (properties is IDictionary dictionary) value = dictionary.Contains(key) ? dictionary[key] : null;
        else if (Marshal.IsComObject(properties!))
        {
            var type = properties!.GetType();
            if (type.InvokeMember("Exists", BindingFlags.InvokeMethod, null, properties, [key], CultureInfo.InvariantCulture) is not true)
                return null;
            value = type.InvokeMember("Item", BindingFlags.GetProperty, null, properties, [key], CultureInfo.InvariantCulture);
        }
        else throw new COMException("With properties must be a dictionary with name and/or url entries.", Errors.InvalidArgument);
        return value is null ? null : value as string
            ?? throw new COMException("The " + key + " property must be text.", Errors.InvalidArgument);
    }
}

internal static class Errors
{
    internal const int NotFound = unchecked((int)0x8002000B);
    internal const int InvalidArgument = unchecked((int)0x80070057);
    internal const int OperationFailed = unchecked((int)0x8004A001);
    internal const int Unavailable = unchecked((int)0x8004A002);
}
