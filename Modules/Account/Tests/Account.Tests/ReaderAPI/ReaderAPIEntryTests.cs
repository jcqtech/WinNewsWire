using System.Globalization;
using System.Text.Json;
using WinNewsWire.ReaderAPI;
using Xunit;

namespace WinNewsWire.Account.Tests.ReaderAPI;

public class ReaderAPIEntryTests
{
    [Fact]
    public void LowIdConvertsToDecimal()
        => Assert.Equal("1560279178774793", Entry("tag:google.com,2005:reader/item/00058b10ce338909").UniqueID(ReaderAPIVariant.FreshRSS));

    [Fact]
    public void HighBitIdDoesNotOverflow()
        => Assert.Equal("-12817", Entry("tag:google.com,2005:reader/item/ffffffffffffcdef").UniqueID(ReaderAPIVariant.FreshRSS));

    [Fact]
    public void ZeroId()
        => Assert.Equal("0", Entry("tag:google.com,2005:reader/item/0000000000000000").UniqueID(ReaderAPIVariant.FreshRSS));

    [Fact]
    public void NonHexIdReturnsArticleIdUnchanged()
    {
        const string id = "tag:google.com,2005:reader/item/notavalidhexvalue";
        Assert.Equal(id, Entry(id).UniqueID(ReaderAPIVariant.FreshRSS));
    }

    [Fact]
    public void TheOldReaderReturnsRawIdPart()
        => Assert.Equal("00058b10ce338909", Entry("tag:google.com,2005:reader/item/00058b10ce338909").UniqueID(ReaderAPIVariant.TheOldReader));

    [Fact]
    public void DecimalRoundTripsToOriginalHex()
    {
        foreach (var hex in new[] { "00058b10ce338909", "ffffffffffffcdef", "0000000000000000" })
        {
            var value = Entry("tag:google.com,2005:reader/item/" + hex).UniqueID(ReaderAPIVariant.FreshRSS);
            Assert.Equal(hex, unchecked((ulong)long.Parse(value, CultureInfo.InvariantCulture)).ToString("x16", CultureInfo.InvariantCulture));
            Assert.Equal("tag:google.com,2005:reader/item/" + hex, ReaderAPICaller.ItemID(value, ReaderAPIVariant.FreshRSS));
        }
    }

    private static ReaderAPIEntry Entry(string id)
        => JsonSerializer.Deserialize<ReaderAPIEntry>("{\"id\":\"" + id + "\",\"summary\":{},\"categories\":[],\"origin\":{}}")!;
}
