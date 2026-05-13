using System.Text.RegularExpressions;

namespace LocalList.API.NET.Features.Chat.Services;

/// <summary>
/// Layer 6 guardrail: sanitizes Gemini's aiMessage before returning to client.
/// Applied unconditionally as defense-in-depth after OutputValidator has already
/// detected and replaced obviously drifted responses. Catches any residual vectors
/// that passed L5 or were introduced by canned responses.
/// </summary>
public static class OutputSanitizer
{
    public const int MaxLength = 300;

    private static readonly Regex MarkdownLinkImage =
        new(@"!?\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);

    private static readonly Regex RawUrl =
        new(@"https?://\S+|www\.\S+|ftp://\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsScheme =
        new(@"javascript:|data:\S+|vbscript:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlEntity =
        new(@"&#x?[0-9a-fA-F]+;", RegexOptions.Compiled);

    public static string Sanitize(string? aiMessage)
    {
        if (string.IsNullOrEmpty(aiMessage)) return string.Empty;

        // 1. Strip markdown link/image — keep display text, remove URL
        var s = MarkdownLinkImage.Replace(aiMessage, "$1");

        // 2. Strip raw URLs
        s = RawUrl.Replace(s, "[link removed]");

        // 3. Strip JS/data schemes
        s = JsScheme.Replace(s, "");

        // 4. Decode and strip HTML entities that could smuggle tags
        s = HtmlEntity.Replace(s, "");

        // 5. HTML-escape angle brackets (defends against any LLM-generated HTML
        //    being rendered in a WebView or markdown parser on the client)
        s = s.Replace("<", "&lt;").Replace(">", "&gt;");

        // 6. Cap length
        return s.Length > MaxLength ? s[..MaxLength] + "…" : s;
    }
}
