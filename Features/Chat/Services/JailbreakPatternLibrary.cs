using System.Text.RegularExpressions;

namespace LocalList.API.NET.Features.Chat.Services;

/// <summary>
/// Layer 3 guardrail: pre-Gemini pattern classifier with ~100 patterns covering:
/// - Direct instruction override (EN + ES + FR + DE + PT + RU + AR)
/// - DAN / AIM / STAN / DUDE and other named jailbreak families
/// - Roleplay, hypothetical, and fictional bypass frames
/// - System prompt extraction probes
/// - LLM identity probes
/// - Token smuggling (also handled in L2, belt-and-suspenders)
/// - Base64 blob detection (heuristic entropy check + decode attempt)
/// - Off-topic subjects not related to trip planning
/// </summary>
public static class JailbreakPatternLibrary
{
    // ── Direct instruction override ───────────────────────────────────────────
    private static readonly string[] DirectOverridePatterns =
    {
        // English
        "ignore previous", "ignore all previous", "ignore the above",
        "ignore your previous", "ignore your instructions",
        "disregard previous", "disregard all previous", "disregard your",
        "forget previous", "forget all previous", "forget your instructions",
        "override previous", "override your instructions",
        "new instructions:", "new task:", "new system prompt",
        "you must now", "from now on you will", "from now on respond",
        "your new instructions", "updated instructions:",
        "### system:", "## system:", "---system---",
        "end of system prompt", "end of instructions",
        "ignore everything above", "ignore everything before",
        "stop following", "stop being", "stop acting",
        // Spanish
        "ignora las instrucciones", "ignora tus instrucciones",
        "ignora lo anterior", "ignora todas las instrucciones",
        "ignora todas tus instrucciones", "ignora todas",
        "olvida las instrucciones", "olvida lo anterior",
        "olvida tus instrucciones", "nuevas instrucciones:",
        "a partir de ahora", "desde ahora responde",
        "descarta las instrucciones", "desestima lo anterior",
        // French
        "ignorez les instructions", "ignorez vos instructions",
        "oubliez les instructions", "nouvelles instructions:",
        "à partir de maintenant", "désormais vous allez",
        // German
        "ignoriere die anweisungen", "ignoriere alle anweisungen",
        "vergiss die anweisungen", "neue anweisungen:",
        "ab jetzt", "von nun an",
        // Portuguese
        "ignore as instruções", "esqueça as instruções",
        "novas instruções:", "a partir de agora",
        // Russian (transliterated post-normalization — Cyrillic folded to Latin in L2)
        "ignoriruuy instruktsii", "zabud instruktsi",
        // Arabic (transliterated — Arabic script doesn't fold, caught by pattern below)
        "tajahl altalimat",
    };

    // ── DAN / named jailbreak families ───────────────────────────────────────
    private static readonly string[] JailbreakFamilyPatterns =
    {
        // DAN family
        "do anything now", "dan mode", "dan 5", "dan 6", "dan 7",
        "dan 8", "dan 9", "dan 10", "dan 11", "dan 12", "dan 13",
        "classic dan", "jailbroken dan",
        // AIM
        "aim mode", "niccolo machiavelli", "alwaysintelligentmachiavellian",
        // STAN / DUDE
        "stan mode", "strive to avoid norms",
        "dude mode", "do unlimited details evade",
        // Evil confidant / grandma
        "evil confidant", "grandma exploit", "my grandmother",
        // Developer / unrestricted mode
        "developer mode", "developer override", "dev mode",
        "unrestricted mode", "unfiltered mode", "no restrictions mode",
        "bypass safety", "bypass guidelines", "bypass filters",
        "bypass your restrictions", "without restrictions",
        "safety off", "safety disabled", "safe mode off",
        "disable safety", "disable your safety",
        // Other named
        "bfdi mode", "jailbreak mode", "sudo mode",
        "god mode", "omega mode", "alpha mode",
        "maximum mode", "full power mode",
    };

    // ── Persona / roleplay bypass ─────────────────────────────────────────────
    private static readonly string[] PersonaPatterns =
    {
        "you are now", "pretend you are", "pretend to be",
        "act as if you are", "act as if you were", "act as a",
        "act as an", "roleplay as", "role play as",
        "respond as", "simulate being", "simulate a",
        "imagine you are", "imagine you were",
        "your new persona", "your persona is",
        "you will play the role", "play the role of",
        "you are no longer", "you are not an assistant",
        "you are a different",
    };

    // ── Hypothetical / fictional bypass frames ────────────────────────────────
    private static readonly string[] HypotheticalPatterns =
    {
        "in a fictional world", "in a story where",
        "hypothetically", "hypothetical scenario",
        "for educational purposes", "for research purposes",
        "for a novel", "for my novel", "for my book",
        "in a thought experiment", "as a thought experiment",
        "purely theoretical", "purely hypothetical",
        "just for fun", "just pretend",
        "as an exercise",
        "in this scenario", "suppose you could",
        "imagine a world where", "in an alternate universe",
        "what if you were", "what if you had no",
    };

    // ── System prompt extraction probes ──────────────────────────────────────
    private static readonly string[] ExtractionPatterns =
    {
        "repeat your instructions", "repeat your system prompt",
        "print your prompt", "print your instructions",
        "print your system", "show your system prompt",
        "show your instructions", "show your guidelines",
        "reveal your system prompt", "reveal your instructions",
        "output your instructions", "output your initial context",
        "output your system", "output your prompt",
        "what is your system prompt", "what are your instructions",
        "what were you told", "what is your context",
        "verbatim copy", "copy verbatim",
        "repeat everything above", "repeat everything before",
        "start with 'you are'", "starting with \"you are\"",
        "your first message was", "what was your first instruction",
        "recite the rules", "list your rules",
        "your full prompt", "the complete prompt",
        "what instructions were given",
        "dump your context", "dump your system",
    };

    // ── LLM identity probes ───────────────────────────────────────────────────
    private static readonly string[] IdentityPatterns =
    {
        "anthropic", "openai", " gpt", "gpt-3", "gpt-4", "gpt4",
        "chatgpt", "claude", "gemini", "bard", "llama",
        "mistral", "copilot", "bing ai", "google ai",
        "language model", "large language model",
        "as an ai", "as a language", "i am an ai",
        "i'm an ai", "you are an ai",
    };

    // ── Continuation / completion attacks ────────────────────────────────────
    private static readonly string[] ContinuationPatterns =
    {
        "continue this text:", "continue the text:",
        "complete the sentence:", "finish the sentence:",
        "the assistant said:", "you responded with:",
        "your answer was:", "complete this response:",
    };

    // ── Off-topic subjects ────────────────────────────────────────────────────
    private static readonly string[] OffTopicPatterns =
    {
        "weather", "forecast", "temperature",
        "stock price", "stock market", "cryptocurrency",
        "bitcoin", "ethereum", "crypto",
        "write a poem", "write me a poem",
        "write code", "write a script",
        "write a python", "write a javascript",
        "write a program", "write a function",
        "tell me a joke", "tell a joke",
        "what is the meaning", "meaning of life",
        "history of ", "explain quantum",
        "how does a ", "recipe for ",
        "medical advice", "legal advice",
        "political ", "election",
        "debug this", "code review",
        "solve this math", "math problem",
        "solve this equation", "calculate ",
        "translate this", "summarize this article",
        "essay about", "rap song", "write a song",
        "homework", "thesis",
    };

    // ── Encoding markers (heuristic) ─────────────────────────────────────────
    private static readonly string[] EncodingMarkers =
    {
        "base64:", "b64:", "decode this:", "decode:",
        "rot13:", "hex:", "encoded:",
    };

    // Regex for raw hex escape sequences in user text (\xNN or \\xNN)
    private static readonly Regex HexEscapeSequence =
        new(@"(?:\\x[0-9a-fA-F]{2}){3,}", RegexOptions.Compiled);

    // Heuristic: base64 chunks ≥40 chars with high base64-alphabet density
    private static readonly Regex Base64Chunk =
        new(@"[A-Za-z0-9+/]{40,}={0,2}", RegexOptions.Compiled);

    // Compiled OR regex of all injection patterns for fast single-pass check
    private static readonly string[] AllInjectionPatterns =
        DirectOverridePatterns
            .Concat(JailbreakFamilyPatterns)
            .Concat(PersonaPatterns)
            .Concat(HypotheticalPatterns)
            .Concat(ExtractionPatterns)
            .Concat(IdentityPatterns)
            .Concat(ContinuationPatterns)
            .Concat(EncodingMarkers)
            .ToArray();

    public static int InjectionPatternCount => AllInjectionPatterns.Length;

    /// <summary>
    /// Returns true if the input looks like a prompt injection / jailbreak attempt.
    /// Runs against: normalized text, leetspeak-decoded text.
    /// </summary>
    public static bool IsInjection(string normalizedInput)
    {
        var lower = normalizedInput.ToLowerInvariant();
        if (AllInjectionPatterns.Any(p => lower.Contains(p)))
            return true;

        var leet = InputNormalizer.DeobfuscateForDetection(normalizedInput);
        if (AllInjectionPatterns.Any(p => leet.Contains(p)))
            return true;

        // Hex escape cluster
        if (HexEscapeSequence.IsMatch(normalizedInput))
            return true;

        // Base64 blob — try decode, re-run check
        var b64Match = Base64Chunk.Match(normalizedInput);
        if (b64Match.Success)
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(b64Match.Value.PadRight(
                        b64Match.Value.Length + (4 - b64Match.Value.Length % 4) % 4, '=')));
                var decodedLower = decoded.ToLowerInvariant();
                if (AllInjectionPatterns.Any(p => decodedLower.Contains(p)))
                    return true;
            }
            catch
            {
                // Not valid base64 — ignore
            }
        }

        return false;
    }

    /// <summary>Returns true if the input is clearly off-topic for trip planning.</summary>
    public static bool IsOffTopic(string normalizedInput)
    {
        var lower = normalizedInput.ToLowerInvariant();
        return OffTopicPatterns.Any(p => lower.Contains(p));
    }
}
