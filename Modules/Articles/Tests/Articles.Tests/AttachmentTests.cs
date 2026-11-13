using System.Text.Json;
using Xunit;

namespace WinNewsWire.Articles.Tests;

public class AttachmentTests
{
    [Fact]
    public void MetadataRoundTripsAndParticipatesInEquality()
    {
        var attachment = Attachment.Create(null, "https://example.com/video", "video/mp4", "Title", 4294967296L, 120,
            1920, 1080, Attachment.AttachmentMedium.Video)!;
        var decoded = Assert.Single(Attachment.FromJson(Attachment.ToJson([attachment]))!);
        Assert.Equal(attachment, decoded);
        Assert.Equal(1920, decoded.Width);
        Assert.Equal(1080, decoded.Height);
        Assert.Equal(4294967296L, decoded.SizeInBytes);
        Assert.Equal(Attachment.AttachmentMedium.Video, decoded.Medium);
        Assert.NotEqual(attachment, Attachment.Create(null, attachment.URL, attachment.MimeType, "Changed", attachment.SizeInBytes,
            attachment.DurationInSeconds, attachment.Width, attachment.Height, attachment.Medium));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"url\":\"\"}]")]
    [InlineData("[{\"url\":\"https://example.com/\",\"medium\":1}]")]
    public void MalformedStoredAttachmentsFailExplicitly(string json)
        => Assert.Throws<JsonException>(() => Attachment.FromJson(json));
}
