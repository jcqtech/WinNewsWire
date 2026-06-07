using Markdig;

namespace WinNewsWire.Markdown;

/// <summary>
/// Port of RSMarkdown. Thin wrapper over Markdig for Markdown → HTML conversion.
/// </summary>
public static class Markdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>Convert Markdown text to HTML.</summary>
    public static string ToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;
        return Markdig.Markdown.ToHtml(markdown, Pipeline);
    }
}
