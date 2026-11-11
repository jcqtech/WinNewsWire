using System.Text.Json.Serialization;

namespace WinNewsWire.NewsBlur;

public sealed record NewsBlurLoginResponse(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("authenticated")] bool Authenticated);
