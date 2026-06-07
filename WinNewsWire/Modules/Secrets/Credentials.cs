namespace WinNewsWire.Secrets;

/// <summary>Port of <c>CredentialsType</c>.</summary>
public enum CredentialsType
{
    Basic,
    NewsBlurBasic,
    NewsBlurSessionId,
    ReaderBasic,
    ReaderApiKey,
    OAuthAccessToken,
    OAuthAccessTokenSecret,
    OAuthRefreshToken,
}

/// <summary>Port of <c>Credentials</c>.</summary>
public sealed record Credentials(CredentialsType Type, string Username, string Secret);

public sealed class CredentialsException : Exception
{
    public CredentialsException(string message) : base(message) { }
}
