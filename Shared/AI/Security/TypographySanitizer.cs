using System.Text.RegularExpressions;

namespace LocalList.API.NET.Shared.AI.Security;

/// <summary>
/// Brand guardrail: the LocalList brand bans long dashes (em-dash and friends) in
/// user-visible copy. Only the LLM (and Google editorial summaries) introduce them,
/// so we normalize them to a plain hyphen-minus at every AI/external write choke point.
/// This is the single source of truth for that normalization — the older inline
/// <c>.Replace("—","-").Replace("–","-")…</c> chains all delegate here.
/// </summary>
public static class TypographySanitizer
{
    // U+2012 figure dash, U+2013 en dash, U+2014 em dash, U+2015 horizontal bar,
    // U+2212 minus sign → all normalized to U+002D hyphen-minus (a hyphen, not a comma).
    private static readonly Regex LongDashes =
        new("[‒–—―−]", RegexOptions.Compiled);

    // Collapse any run of 2+ spaces directly adjacent to a hyphen down to a single
    // space, so "food  —  perfect" reads cleanly as "food - perfect" (no double space).
    private static readonly Regex DoubledSpacesAroundHyphen =
        new(@"(?<=-) {2,}| {2,}(?=-)", RegexOptions.Compiled);

    /// <summary>
    /// Replaces em-dash / en-dash / figure-dash / horizontal-bar / minus-sign with a
    /// plain hyphen-minus, then collapses any doubled spaces around the result.
    /// Null/empty input is returned unchanged.
    /// </summary>
    public static string? StripLongDashes(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var replaced = LongDashes.Replace(s, "-");
        return DoubledSpacesAroundHyphen.Replace(replaced, " ");
    }
}
