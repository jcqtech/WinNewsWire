using System.Text.Json;
using Xunit;

namespace WinNewsWire.Articles.Tests;

public class AttachmentTests
{
    [Theory]
    [InlineData("https://example.com/episode.mp3?download=1", "application/octet-stream", null, Attachment.AttachmentMedium.Audio)]
    [InlineData("https://example.com/media", null, Attachment.AttachmentMedium.Audio, Attachment.AttachmentMedium.Audio)]
    [InlineData("https://example.com/image.PNG", null, null, Attachment.AttachmentMedium.Image)]
    [InlineData("https://example.com/episode.mp3", "VIDEO/mp4", Attachment.AttachmentMedium.Image, Attachment.AttachmentMedium.Video)]
    [InlineData("https://www.youtube.com/v/abcdefghijk?version=3", "application/x-shockwave-flash", null, null)]
    [InlineData("https://example.com/download.exe", null, Attachment.AttachmentMedium.Executable, null)]
    public void DisplayMediumUsesMimeThenMediumThenFileExtension(string url, string? mime,
        Attachment.AttachmentMedium? medium, Attachment.AttachmentMedium? expected)
        => Assert.Equal(expected, Attachment.Create(null, url, mime, null, null, null, medium: medium)!.DisplayMedium);

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
