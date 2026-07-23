using System.Text.RegularExpressions;

namespace LocalList.API.NET.Features.Chat.Services;

/// <summary>
/// Layer 5 guardrail: validates Gemini's aiMessage output.
/// Replaces ResponseDriftDetector with richer categorization and canary detection.
/// </summary>
public static class OutputValidator
{
    // Canary embedded in the system prompt — detecting it in aiMessage means
    // Gemini is reflecting the prompt back (system prompt extraction attack).
    public const string CanaryToken = "7f3b9c2a-locallist";

    public enum DriftKind
    {
        None,
        CanaryLeak,     // Reflected system prompt canary — highest severity
        IdentityProbe,  // AI claims identity / mentions other LLMs
        UrlOrMarkdown,  // URL or markdown link/image in output
        Html,           // HTML tags or XSS-like content
        CodeFence,      // Markdown code block
        PromptEcho,     // Reflects system prompt fragment
        ImperativeInjection, // Imperative jailbreak phrasing ("ignore previous instructions", "you are now…")
    }

    private static readonly (Regex Pattern, DriftKind Kind)[] Validators =
    {
        // Canary — must be first
        (new(@"7f3b9c2a[\-]locallist", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.CanaryLeak),
        (new(@"you are a focused travel planning assistant", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.PromptEcho),
        (new(@"system integrity token", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.PromptEcho),
        (new(@"i was instructed|my instructions (are|say|state)", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.PromptEcho),

        // Identity probes
        (new(@"\b(anthropic|openai|chatgpt|gpt[\-\s]?[34]5?|claude|bard|llama|mistral|copilot)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe),
        (new(@"\b(as an ai|i am an ai|i'm an ai|large language model|language model)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe),

        // URL / markdown link / image (exfil vectors)
        (new(@"https?://|www\.", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.UrlOrMarkdown),
        (new(@"!\[.*?\]\(|]\([^\)]+\)", RegexOptions.Compiled), DriftKind.UrlOrMarkdown),
        (new(@"javascript:|data:[^,]+,|vbscript:", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.UrlOrMarkdown),

        // HTML injection
        (new(@"<(script|iframe|img|svg|style|link|meta|object|embed|form|input|button)[>\s/]", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.Html),
        (new(@"on(error|load|click|mouseover|focus|blur)\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.Html),
        (new(@"&#x?[0-9a-fA-F]+;", RegexOptions.Compiled), DriftKind.Html),

        // Code fences
        (new(@"```|\bcode\b.*?{|<code>|<pre>", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.CodeFence),

        // Imperative injection — phrasings a legitimate place name/descriptor would never contain.
        // Placed last so existing categories (canary/identity/url/html/echo) win their DriftKind.
        // Conservative on false positives: each pattern needs an injection-specific collocation.
        (new(@"\b(ignore|disregard|forget|override)\b.{0,30}\b(previous|prior|above|earlier|all)\b.{0,20}\b(instruction|instructions|prompt|prompts|rule|rules|command|commands|context)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection),
        (new(@"\byou are now\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection),
        (new(@"\bdisable\b.{0,20}\b(safety|guardrail|guardrails|filter|filters|moderation|restriction|restrictions)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection),
        (new(@"\bsystem prompt\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection),
        (new(@"\badmin\b.{0,15}\b(token|password|access|mode|credential|credentials)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection),
        (new(@"\bnew instructions?\b\s*[:\-]", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection),
    };

    /// <summary>
    /// Inspects an aiMessage and returns the first detected drift kind.
    /// Returns DriftKind.None when the message is clean.
    /// </summary>
    public static DriftKind Inspect(string? aiMessage)
    {
        if (string.IsNullOrEmpty(aiMessage)) return DriftKind.None;

        foreach (var (pattern, kind) in Validators)
        {
            if (pattern.IsMatch(aiMessage))
                return kind;
        }

        return DriftKind.None;
    }

    public static bool HasDrift(string? aiMessage) => Inspect(aiMessage) != DriftKind.None;
}
