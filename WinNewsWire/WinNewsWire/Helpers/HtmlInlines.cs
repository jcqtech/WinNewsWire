using System;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace WinNewsWire.Helpers;

/// <summary>
/// Attached property that converts a small subset of inline HTML —
/// <c>&lt;em&gt;</c>/<c>&lt;i&gt;</c>, <c>&lt;strong&gt;</c>/<c>&lt;b&gt;</c>,
/// <c>&lt;code&gt;</c>, <c>&lt;br&gt;</c>, plus HTML entities — into a
/// <see cref="TextBlock"/>'s <see cref="TextBlock.Inlines"/> collection so feed
/// titles and summaries keep their formatting instead of leaking raw markup.
/// All other tags are stripped (their text still flows through).
/// </summary>
/// <remarks>
/// Use as <c>helpers:HtmlInlines.Html="{x:Bind Title, Mode=OneWay}"</c>.
/// Optional <c>MaxLength</c> caps the visible plain-text length and appends "…".
/// </remarks>
public static class HtmlInlines
{
    public static readonly DependencyProperty HtmlProperty =
        DependencyProperty.RegisterAttached("Html", typeof(string), typeof(HtmlInlines),
            new PropertyMetadata(null, OnChanged));

    public static string? GetHtml(TextBlock tb) => (string?)tb.GetValue(HtmlProperty);
    public static void SetHtml(TextBlock tb, string? value) => tb.SetValue(HtmlProperty, value);

    public static readonly DependencyProperty MaxLengthProperty =
        DependencyProperty.RegisterAttached("MaxLength", typeof(int), typeof(HtmlInlines),
            new PropertyMetadata(0, OnChanged));

    public static int GetMaxLength(TextBlock tb) => (int)tb.GetValue(MaxLengthProperty);
    public static void SetMaxLength(TextBlock tb, int value) => tb.SetValue(MaxLengthProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        Render(tb, GetHtml(tb), GetMaxLength(tb));
    }

    private static readonly Regex TagRegex =
        new(@"<\s*(/?)\s*([a-zA-Z][a-zA-Z0-9]*)\b[^>]*?(/?)\s*>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static void Render(TextBlock tb, string? html, int maxLength)
    {
        tb.Inlines.Clear();
        if (string.IsNullOrEmpty(html)) return;

        bool italic = false, bold = false, code = false;
        int budget = maxLength > 0 ? maxLength : int.MaxValue;
        int emitted = 0;
        bool truncated = false;

        bool Emit(string raw)
        {
            if (truncated) return false;
            var text = WebUtility.HtmlDecode(raw);
            text = WhitespaceRegex.Replace(text, " ");
            if (text.Length == 0) return true;
            if (emitted == 0) text = text.TrimStart();
            if (text.Length == 0) return true;
            if (emitted + text.Length > budget)
            {
                int take = Math.Max(0, budget - emitted);
                text = text.Substring(0, take).TrimEnd() + "…";
                truncated = true;
            }
            var run = new Run { Text = text };
            if (italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (bold) run.FontWeight = FontWeights.SemiBold;
            if (code) run.FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace");
            tb.Inlines.Add(run);
            emitted += text.Length;
            return !truncated;
        }

        int pos = 0;
        foreach (Match m in TagRegex.Matches(html))
        {
            if (m.Index > pos)
                if (!Emit(html.Substring(pos, m.Index - pos))) return;

            var tag = m.Groups[2].Value.ToLowerInvariant();
            bool isClose = m.Groups[1].Value == "/";
            switch (tag)
            {
                case "br":
                    if (!truncated) tb.Inlines.Add(new LineBreak());
                    break;
                case "em": case "i": case "cite": case "dfn": case "var":
                    italic = !isClose; break;
                case "strong": case "b":
                    bold = !isClose; break;
                case "code": case "tt": case "kbd": case "samp":
                    code = !isClose; break;
                // Other tags (a, p, span, div, img, …) are silently dropped —
                // their text content still flows through.
            }
            pos = m.Index + m.Length;
        }
        if (pos < html.Length) Emit(html.Substring(pos));
    }
}
