using System;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WinNewsWire.AppShared.Extensions;

namespace WinNewsWire.Helpers;

public static class HtmlInlines
{
    public static readonly DependencyProperty HtmlProperty =
        DependencyProperty.RegisterAttached("Html", typeof(string), typeof(HtmlInlines), new PropertyMetadata(null, OnChanged));
    public static string? GetHtml(TextBlock textBlock) => (string?)textBlock.GetValue(HtmlProperty);
    public static void SetHtml(TextBlock textBlock, string? value) => textBlock.SetValue(HtmlProperty, value);

    public static readonly DependencyProperty AttributedTextProperty =
        DependencyProperty.RegisterAttached("AttributedText", typeof(AttributedText), typeof(HtmlInlines), new PropertyMetadata(null, OnChanged));
    public static AttributedText? GetAttributedText(TextBlock textBlock) => (AttributedText?)textBlock.GetValue(AttributedTextProperty);
    public static void SetAttributedText(TextBlock textBlock, AttributedText? value) => textBlock.SetValue(AttributedTextProperty, value);

    public static readonly DependencyProperty MaxLengthProperty =
        DependencyProperty.RegisterAttached("MaxLength", typeof(int), typeof(HtmlInlines), new PropertyMetadata(0, OnChanged));
    public static int GetMaxLength(TextBlock textBlock) => (int)textBlock.GetValue(MaxLengthProperty);
    public static void SetMaxLength(TextBlock textBlock, int value) => textBlock.SetValue(MaxLengthProperty, value);

    private static void OnChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is TextBlock textBlock)
            Render(textBlock, GetAttributedText(textBlock) ?? SimpleHtml.Parse(GetHtml(textBlock) ?? ""), GetMaxLength(textBlock));
    }

    internal static void Render(TextBlock textBlock, AttributedText text, int maxLength = 0)
    {
        textBlock.Inlines.Clear();
        var limit = text.Text.Length;
        if (maxLength > 0)
        {
            var characters = StringInfo.ParseCombiningCharacters(text.Text);
            if (characters.Length > maxLength) limit = characters[maxLength];
        }
        if (text.Runs.All(run => run.Style == InlineTextStyle.None))
        {
            textBlock.Text = limit < text.Text.Length ? text.Text[..limit] + "\u2026" : text.Text;
            return;
        }
        var offset = 0;
        foreach (var item in text.Runs)
        {
            if (offset >= limit) break;
            var length = Math.Min(item.Text.Length, limit - offset);
            var run = new Run { Text = item.Text[..length] };
            var style = item.Style;
            if (style.HasFlag(InlineTextStyle.Bold)) run.FontWeight = FontWeights.Bold;
            if (style.HasFlag(InlineTextStyle.Italic)) run.FontStyle = global::Windows.UI.Text.FontStyle.Italic;
            if (style.HasFlag(InlineTextStyle.Monospace)) run.FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace");
            if (style.HasFlag(InlineTextStyle.Underline)) run.TextDecorations |= global::Windows.UI.Text.TextDecorations.Underline;
            if (style.HasFlag(InlineTextStyle.Strikethrough)) run.TextDecorations |= global::Windows.UI.Text.TextDecorations.Strikethrough;
            if (style.HasFlag(InlineTextStyle.Superscript)) Typography.SetVariants(run, FontVariants.Superscript);
            else if (style.HasFlag(InlineTextStyle.Subscript)) Typography.SetVariants(run, FontVariants.Subscript);
            textBlock.Inlines.Add(run);
            offset += length;
        }
        if (limit < text.Text.Length) textBlock.Inlines.Add(new Run { Text = "\u2026" });
    }
}
