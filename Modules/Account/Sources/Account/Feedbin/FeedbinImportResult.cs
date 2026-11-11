using System.Text.Json.Serialization;

namespace WinNewsWire.Feedbin;

public sealed record FeedbinImportResult(
    [property: JsonPropertyName("id"), JsonRequired] int ImportResultID,
    [property: JsonPropertyName("complete"), JsonRequired] bool Complete);
