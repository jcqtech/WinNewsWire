namespace WinNewsWire.Account;

public enum AccountType
{
    OnMyMac = 1,
    CloudKit = 2,
    Feedly = 16,
    Feedbin = 17,
    NewsBlur = 19,
    FreshRSS = 20,
    Inoreader = 21,
    BazQux = 22,
    TheOldReader = 23,
}

public static class AccountTypeExtensions
{
    public static string DisplayName(this AccountType type) => type switch
    {
        AccountType.OnMyMac => "On My PC",
        AccountType.CloudKit => "iCloud",
        AccountType.Feedly => "Feedly",
        AccountType.Feedbin => "Feedbin",
        AccountType.NewsBlur => "NewsBlur",
        AccountType.FreshRSS => "FreshRSS",
        AccountType.Inoreader => "Inoreader",
        AccountType.BazQux => "BazQux",
        AccountType.TheOldReader => "The Old Reader",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
