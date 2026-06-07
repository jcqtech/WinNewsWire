namespace WinNewsWire.Core;

/// <summary>
/// Thrown when <see cref="MacroProcessor"/> is constructed with an empty
/// delimiter — protects callers from accidentally matching every character
/// when they forget to pass <c>[[</c>/<c>]]</c>.
/// </summary>
public sealed class MacroProcessorException : Exception
{
    public MacroProcessorException(string message) : base(message) { }
}

/// <summary>
/// Port of NetNewsWire RSCore's <c>MacroProcessor</c>. Substitutes
/// <c>[[key]]</c>-style macros in a template string with values from a
/// dictionary. Used by the article renderer (and other template-driven
/// surfaces) to materialise HTML from a per-theme template.
/// </summary>
/// <remarks>
/// Replacement is non-recursive: if a substitution value itself contains a
/// macro, the inner macro is preserved verbatim. Unknown keys are left as
/// the original <c>[[key]]</c> token so callers can spot typos.
/// </remarks>
public sealed class MacroProcessor
{
    private readonly string _template;
    private readonly IReadOnlyDictionary<string, string> _substitutions;
    private readonly string _macroStart;
    private readonly string _macroEnd;

    public MacroProcessor(string template,
        IReadOnlyDictionary<string, string> substitutions,
        string macroStart = "[[",
        string macroEnd = "]]")
    {
        if (string.IsNullOrEmpty(macroStart))
            throw new MacroProcessorException("macroStart cannot be empty.");
        if (string.IsNullOrEmpty(macroEnd))
            throw new MacroProcessorException("macroEnd cannot be empty.");

        _template = template;
        _substitutions = substitutions;
        _macroStart = macroStart;
        _macroEnd = macroEnd;
    }

    /// <summary>Returns the template with macros expanded.</summary>
    public string RenderedText => ProcessMacros();

    /// <summary>
    /// One-shot helper that mirrors the Swift <c>renderedText(...)</c> static.
    /// Construct and render in a single call.
    /// </summary>
    public static string RenderedTextWith(
        string template,
        IReadOnlyDictionary<string, string> substitutions,
        string macroStart = "[[",
        string macroEnd = "]]")
        => new MacroProcessor(template, substitutions, macroStart, macroEnd).RenderedText;

    private string ProcessMacros()
    {
        var result = new System.Text.StringBuilder(_template.Length);
        int index = 0;

        while (true)
        {
            int startIndex = _template.IndexOf(_macroStart, index, StringComparison.Ordinal);
            if (startIndex < 0) break;

            result.Append(_template, index, startIndex - index);

            int keyStart = startIndex + _macroStart.Length;
            int endIndex = _template.IndexOf(_macroEnd, keyStart, StringComparison.Ordinal);
            if (endIndex < 0)
            {
                // Unterminated macro — bail out and let the remaining template
                // copy through as-is so the caller can see the broken token.
                index = startIndex;
                break;
            }

            var key = _template.Substring(keyStart, endIndex - keyStart);
            var replacement = _substitutions.TryGetValue(key, out var v)
                ? v
                : _macroStart + key + _macroEnd;
            result.Append(replacement);

            index = endIndex + _macroEnd.Length;
        }

        result.Append(_template, index, _template.Length - index);
        return result.ToString();
    }
}
