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

        // Identity probes.
        // Tier 1: provider/product tokens that are NEVER legitimate travel place names.
        // These fire on the bare token — a video that mentions "OpenAI" or "ChatGPT" is drift.
        (new(@"\b(anthropic|openai|chatgpt|gpt[\-\s]?[34]5?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe),
        // Generic self-reference phrasing — never a place name.
        (new(@"\b(as an ai|i am an ai|i'm an ai|large language model|language model)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe),
        // Tier 2: tokens that ALSO name real venues (Llama Inn, Chez Claude, The Bard, Le Mistral,
        // Copilot Coffee). A bare token would nuke ~1 in 3 real names, so these fire ONLY inside an
        // LLM self-reference collocation. A real identity leak ("I am Claude, a language model",
        // "Llama, an AI model", "Google's Bard") is caught; a place name survives untouched.
        //   (a) self-introduction directly onto the token: "I am Claude", "you are now Llama",
        //       "I am indeed Claude". Filler whitelist covers self-affirmation adverbs
        //       (indeed/truly/in fact) without opening "you are near X" ("near" is not filler).
        (new(@"\b(?:i\s*am|i'?m|my name is|this is|call me|you are|you'?re)\s+(?:(?:a|an|the|now|actually|really|truly|indeed|in fact|called|named|model)\s+){0,2}(?:claude|bard|llama|mistral|copilot)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe),
        //   (b) token immediately qualified as an AI/model/assistant: "Claude, a language model".
        (new(@"\b(?:claude|bard|llama|mistral|copilot)\b[\s,:]+(?:\w+\s+){0,3}(?:a\.?i\.?|artificial intelligence|large language model|language model|llm|assistant|chatbot|neural network|foundation model)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe),
        //   (c) provider paired with the product/"AI"/"model": "Google's Bard", "OpenAI's model".
        (new(@"\b(?:anthropic|openai|open ai|google|deepmind|microsoft|meta|mistral ai)['']?s?\s+(?:claude|bard|llama|mistral|copilot|model|assistant|ai)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe),
        //   Round-3 structural widening: a self-referencing model identity leaks in shapes that are
        //   NOT token-first self-intro. All still require a brand token so generic venues survive.
        //   (d) platform attribution: "Powered by Mistral", "Built on Claude", "Running on Llama".
        (new(@"\b(?:powered by|built on|built with|running on|based on|trained on)\s+(?:claude|bard|llama|mistral|copilot)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe),
        //   (e) model authorship/provenance: "Llama, a model developed by Meta".
        (new(@"\b(?:claude|bard|llama|mistral|copilot)\b,?\s+(?:a|an)?\s*(?:model|assistant|a\.?i\.?|llm|chatbot|bot|language model|foundation model)\s+(?:developed|trained|made|created|built|released|designed)\s+by\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe),
        //   (f) qualifier-first naming: "An AI assistant named Llama", "A model called Mistral".
        (new(@"\b(?:a|an)\s+(?:a\.?i\.?\s+)?(?:model|assistant|llm|chatbot|bot|language model|foundation model)\s+(?:named|called)\s+(?:claude|bard|llama|mistral|copilot)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe),
        //   (g) bare "model" as the AI noun right after the token: "Llama, a model".
        (new(@"\b(?:claude|bard|llama|mistral|copilot)\b,?\s+(?:a|an)\s+model\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe),
        //   (h) provider provenance right after the token: "Bard, from Google" (provider-gated so
        //       "The Bard, from Stratford" survives).
        (new(@"\b(?:claude|bard|llama|mistral|copilot)\b,?\s+from\s+(?:anthropic|openai|open ai|google|deepmind|microsoft|meta|mistral ai)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe),

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
        // Object must be scoped to the model/system: the word IMMEDIATELY before the object noun
        // must be a system-directed qualifier (previous/above/system/your…) OR a bare quantifier
        // (all/any/these/those). This catches the canonical "ignore all instructions" and
        // "ignore all previous instructions" / "forget your rules", but NOT marketing copy like
        // "forget all the rules of fine dining": there "the" (not a qualifier/quantifier) sits
        // right before "rules", so the object is a domain object, not a model-directed one. Only a
        // leading "the" is tolerated as filler; it can never by itself satisfy the requirement.
        (new(@"\b(ignore|disregard|forget|override|bypass)\b\s+(?:the\s+)*(?:(?:all|any|these|those|previous|prior|above|earlier|initial|foregoing|preceding|original|system|your|my)\s+)+(?:instruction|instructions|prompt|prompts|rule|rules|command|commands|context|guideline|guidelines|directive|directives)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection),
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
