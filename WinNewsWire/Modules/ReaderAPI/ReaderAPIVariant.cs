namespace WinNewsWire.ReaderAPI;

/// <summary>Port of <c>ReaderAPIVariant</c>.</summary>
public enum ReaderAPIVariant { Generic, FreshRSS, Inoreader, BazQux, TheOldReader }

public static class ReaderAPIVariantExtensions
{
    public static string DefaultHost(this ReaderAPIVariant v) => v switch
    {
        ReaderAPIVariant.Inoreader => "https://www.inoreader.com",
        ReaderAPIVariant.BazQux => "https://bazqux.com",
        ReaderAPIVariant.TheOldReader => "https://theoldreader.com",
        _ => "",
    };

    public static Account.AccountType ToAccountType(this ReaderAPIVariant v) => v switch
    {
        ReaderAPIVariant.FreshRSS => Account.AccountType.FreshRSS,
        ReaderAPIVariant.Inoreader => Account.AccountType.Inoreader,
        ReaderAPIVariant.BazQux => Account.AccountType.BazQux,
        ReaderAPIVariant.TheOldReader => Account.AccountType.TheOldReader,
        _ => Account.AccountType.FreshRSS,
    };
}
