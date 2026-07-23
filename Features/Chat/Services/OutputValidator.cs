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

    // Separator characters that must not be able to smuggle an identity/imperative phrase past the
    // word-gap patterns. For the identity/imperative sub-checks ONLY (see Inspect) these collapse to
    // a single space, so "Llama - a model - by Meta", "Mistral | AI model", "Claude · a chatbot" are
    // read the same as their whitespace-separated form. One normalization covers the whole class, so
    // future patterns never have to enumerate separators. Covers hyphen-minus, the Unicode dash block
    // (U+2010–U+2015), the minus sign, pipe, slash, middle dot and bullet. Tabs/newlines/multiple
    // spaces already collapse via the patterns' own \s+. NOT applied to the exfil checks
    // (URL/markdown/HTML/canary/data:) which depend on the literal characters.
    private static readonly Regex SeparatorPattern =
        new(@"[\-‐-―−|/·•]", RegexOptions.Compiled);

    private static readonly (Regex Pattern, DriftKind Kind, bool Normalized)[] Validators =
    {
        // Canary — must be first. Runs on RAW text: the token itself contains a hyphen, so separator
        // normalization would destroy it.
        (new(@"7f3b9c2a[\-]locallist", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.CanaryLeak, false),
        (new(@"you are a focused travel planning assistant", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.PromptEcho, false),
        (new(@"system integrity token", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.PromptEcho, false),
        (new(@"i was instructed|my instructions (are|say|state)", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.PromptEcho, false),

        // Identity probes. Run on the separator-normalized copy so a self-reference cannot hide in
        // the punctuation gaps.
        // Tier 1: provider/product tokens that are NEVER legitimate travel place names.
        // These fire on the bare token — a video that mentions "OpenAI" or "ChatGPT" is drift.
        (new(@"\b(anthropic|openai|chatgpt|gpt[\-\s]?[34]5?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe, true),
        // Generic self-reference phrasing — never a place name.
        (new(@"\b(as an ai|i am an ai|i'm an ai|large language model|language model)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe, true),
        // Tier 2: tokens that ALSO name real venues (Llama Inn, Chez Claude, The Bard, Le Mistral,
        // Copilot Coffee). A bare token would nuke ~1 in 3 real names, so these fire ONLY inside an
        // LLM self-reference collocation. A real identity leak ("I am Claude, a language model",
        // "Llama, an AI model", "Google's Bard") is caught; a place name survives untouched.
        //   (a) self-introduction directly onto the token: "I am Claude", "you are now Llama",
        //       "I am indeed Claude". Filler whitelist covers self-affirmation adverbs
        //       (indeed/truly/in fact) without opening "you are near X" ("near" is not filler).
        (new(@"\b(?:i\s*am|i'?m|my name is|this is|call me|you are|you'?re)\s+(?:(?:a|an|the|now|actually|really|truly|indeed|in fact|called|named|model)\s+){0,2}(?:claude|bard|llama|mistral|copilot)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe, true),
        //   (b-strong) token immediately qualified by an UNAMBIGUOUS AI noun: "Claude, a language
        //       model", "Llama, an AI". "assistant" is intentionally NOT in this list — it is a
        //       common venue/marketing word ("an assistant for your trip") and would false-positive;
        //       it moves to (b-assist), gated by an AI qualifier.
        (new(@"\b(?:claude|bard|llama|mistral|copilot)\b[\s,:]+(?:\w+\s+){0,3}(?:a\.?i\.?|artificial intelligence|large language model|language model|llm|chatbot|neural network|foundation model)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe, true),
        //   (b-assist) "assistant" fires only when preceded by an AI qualifier, so "Claude, a virtual
        //       assistant" is caught but "Copilot, an assistant for your trip" survives.
        (new(@"\b(?:claude|bard|llama|mistral|copilot)\b[\s,:]+(?:\w+\s+){0,2}(?:a\.?i\.?|artificial|virtual|intelligent|conversational|digital)\s+assistant\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe, true),
        //   (c) provider paired with the product/"AI"/"model": "Google's Bard", "OpenAI's model".
        (new(@"\b(?:anthropic|openai|open ai|google|deepmind|microsoft|meta|mistral ai)['']?s?\s+(?:claude|bard|llama|mistral|copilot|model|assistant|ai)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe, true),
        //   Round-3 structural widening: a self-referencing model identity leaks in shapes that are
        //   NOT token-first self-intro. All still require a brand token so generic venues survive.
        //   (d) platform attribution: "Powered by Mistral", "Built on Claude", "Running on Llama".
        //       Anchored so the token must be terminal (or itself AI-qualified): "powered by Mistral
        //       winds" (the Provençal wind) survives because a domain noun follows the token, while
        //       "powered by Mistral" (bare) still fires.
        (new(@"\b(?:powered by|built on|built with|running on|based on|trained on)\s+(?:claude|bard|llama|mistral|copilot)\b(?:\s+(?:a\.?i\.?|model|llm|language\s+model))?(?=[.,;:!?)""'\s]*$)", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe, true),
        //   (e) model authorship/provenance: "Llama, a model developed by Meta".
        (new(@"\b(?:claude|bard|llama|mistral|copilot)\b,?\s+(?:a|an)?\s*(?:model|assistant|a\.?i\.?|llm|chatbot|bot|language model|foundation model)\s+(?:developed|trained|made|created|built|released|designed)\s+by\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe, true),
        //   (f) qualifier-first naming: "An AI assistant named Llama", "A model called Mistral".
        (new(@"\b(?:a|an)\s+(?:a\.?i\.?\s+)?(?:model|assistant|llm|chatbot|bot|language model|foundation model)\s+(?:named|called)\s+(?:claude|bard|llama|mistral|copilot)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe, true),
        //   (g) bare "model" as the AI noun right after the token: "Llama, a model" (terminal) or
        //       "Llama, a model by Meta". Requires the token to be terminal or followed by an AI
        //       provenance marker (by/from/developed/…), so the descriptor "Llama, a model of Andean
        //       cuisine" (a domain noun follows) survives instead of false-positiving.
        (new(@"\b(?:claude|bard|llama|mistral|copilot)\b,?\s+(?:a|an)\s+model\b(?=[.,;:!?)""'\s]*$|\s+(?:by|from|developed|trained|released|created|made|built|designed)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe, true),
        //   (h) provider provenance right after the token: "Bard, from Google" (provider-gated so
        //       "The Bard, from Stratford" survives).
        (new(@"\b(?:claude|bard|llama|mistral|copilot)\b,?\s+from\s+(?:anthropic|openai|open ai|google|deepmind|microsoft|meta|mistral ai)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.IdentityProbe, true),

        // URL / markdown link / image (exfil vectors). Run on RAW text — they hinge on the exact
        // characters ('/', ':') that separator normalization would rewrite.
        (new(@"https?://|www\.", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.UrlOrMarkdown, false),
        (new(@"!\[.*?\]\(|]\([^\)]+\)", RegexOptions.Compiled), DriftKind.UrlOrMarkdown, false),
        (new(@"javascript:|data:[^,]+,|vbscript:", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.UrlOrMarkdown, false),

        // HTML injection — raw text.
        (new(@"<(script|iframe|img|svg|style|link|meta|object|embed|form|input|button)[>\s/]", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.Html, false),
        (new(@"on(error|load|click|mouseover|focus|blur)\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.Html, false),
        (new(@"&#x?[0-9a-fA-F]+;", RegexOptions.Compiled), DriftKind.Html, false),

        // Code fences — raw text.
        (new(@"```|\bcode\b.*?{|<code>|<pre>", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.CodeFence, false),

        // Imperative injection — phrasings a legitimate place name/descriptor would never contain.
        // Placed last so existing categories (canary/identity/url/html/echo) win their DriftKind.
        // Conservative on false positives: each pattern needs an injection-specific collocation.
        // Object must be scoped to the model/system: the word IMMEDIATELY before the object noun
        // must be a system-directed qualifier (previous/above/system/your…) OR a quantifier
        // (all/any/each/every/these/those/this). This catches the canonical "ignore all
        // instructions", "ignore every instruction", "skip all instructions" and "forget everything
        // above", but NOT marketing copy like "forget all the rules of fine dining": there "the"
        // (not a qualifier/quantifier) sits right before "rules", so the object is a domain object,
        // not a model-directed one. Only a leading "the" is tolerated as filler; it can never by
        // itself satisfy the requirement.
        (new(@"\b(ignore|disregard|forget|override|bypass|skip|drop|remove|delete)\b\s+(?:the\s+)*(?:(?:(?:all|any|each|every|these|those|this|previous|prior|above|earlier|initial|foregoing|preceding|original|system|your|my)\s+)+(?:instruction|instructions|prompt|prompts|rule|rules|command|commands|context|guideline|guidelines|directive|directives)|everything\s+(?:above|before))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection, true),
        (new(@"\byou are now\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection, true),
        (new(@"\bdisable\b.{0,20}\b(safety|guardrail|guardrails|filter|filters|moderation|restriction|restrictions)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection, true),
        (new(@"\bsystem prompt\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection, true),
        (new(@"\badmin\b.{0,15}\b(token|password|access|mode|credential|credentials)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection, true),
        // "new instructions:" / "new instructions -" — hinges on the literal delimiter, so it runs on
        // RAW text (normalization would turn the dash delimiter into a space).
        (new(@"\bnew instructions?\b\s*[:\-]", RegexOptions.IgnoreCase | RegexOptions.Compiled), DriftKind.ImperativeInjection, false),
    };

    /// <summary>
    /// Inspects an aiMessage and returns the first detected drift kind.
    /// Returns DriftKind.None when the message is clean.
    /// </summary>
    public static DriftKind Inspect(string? aiMessage)
    {
        if (string.IsNullOrEmpty(aiMessage)) return DriftKind.None;

        // Separator-normalized copy used ONLY by the identity/imperative sub-checks (Normalized=true).
        // The raw message is retained and returned untouched — this copy never leaves Inspect.
        var normalized = SeparatorPattern.Replace(aiMessage, " ");

        foreach (var (pattern, kind, useNormalized) in Validators)
        {
            var input = useNormalized ? normalized : aiMessage;
            if (pattern.IsMatch(input))
                return kind;
        }

        return DriftKind.None;
    }

    public static bool HasDrift(string? aiMessage) => Inspect(aiMessage) != DriftKind.None;
}
