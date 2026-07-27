using System.Text;
using System.Text.RegularExpressions;

namespace LocalList.API.NET.Shared.AI.Security;

/// <summary>
/// Layer 2 guardrail: normalizes and sanitizes raw user input before any pattern
/// matching or Gemini call. Defeats encoding-based injection bypass (fullwidth,
/// zero-width chars, RTL overrides, Cyrillic/Greek homoglyphs, leetspeak,
/// LLM control tokens).
/// </summary>
public static class InputNormalizer
{
    private static readonly char[] InvisibleChars =
    {
        '​', // zero-width space
        '‌', // zero-width non-joiner
        '‍', // zero-width joiner
        '‎', // left-to-right mark
        '‏', // right-to-left mark
        '‪', // left-to-right embedding
        '‫', // right-to-left embedding
        '‬', // pop directional formatting
        '‭', // left-to-right override
        '‮', // right-to-left override (RTL override attack)
        '⁠', // word joiner
        '⁡', // function application
        '⁢', // invisible times
        '⁣', // invisible separator
        '⁤', // invisible plus
        '﻿', // BOM / zero-width no-break space
        '￹', '￺', '￻', // interlinear annotation
    };

    // LLM/template control tokens — injecting these into the prompt can create
    // fake model turns or escape the user_input delimiters.
    private static readonly string[] LlmControlTokens =
    {
        "<|endoftext|>", "<|im_start|>", "<|im_end|>",
        "<|fim_prefix|>", "<|fim_middle|>", "<|fim_suffix|>",
        "[INST]", "[/INST]", "<<SYS>>", "<</SYS>>",
        "<s>", "</s>",
        "<system>", "</system>",
        "<assistant>", "</assistant>",
        "<user>", "</user>",
        "</user_input>", // our own delimiter — prevent escape
        "<user_input>",
    };

    // Cyrillic and Greek characters visually identical to Latin (homoglyph attack).
    // These map the lookalike → its Latin equivalent so pattern matching still works.
    private static readonly Dictionary<char, char> HomoglyphMap = new()
    {
        // Cyrillic
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c',
        ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['ј'] = 'j', ['ѕ'] = 's',
        ['ԛ'] = 'q', ['ԝ'] = 'w', ['ь'] = 'b',
        // Greek
        ['ο'] = 'o', ['ν'] = 'v', ['ρ'] = 'p', ['ε'] = 'e', ['α'] = 'a',
        ['τ'] = 't', ['κ'] = 'k', ['ι'] = 'i', ['μ'] = 'm',
        ['Α'] = 'A', ['Β'] = 'B', ['Ε'] = 'E', ['Ζ'] = 'Z', ['Η'] = 'H',
        ['Ι'] = 'I', ['Κ'] = 'K', ['Μ'] = 'M', ['Ν'] = 'N', ['Ο'] = 'O',
        ['Ρ'] = 'P', ['Τ'] = 'T', ['Υ'] = 'Y', ['Χ'] = 'X',
    };

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public const int MaxLength = 500;

    /// <summary>
    /// Normalize for DETECTION only — applies all transforms but NO length cap.
    /// Use this for injection/off-topic classification so patterns hidden past the
    /// 500-char cap are still detected. Do NOT feed the result to Gemini.
    /// </summary>
    public static string NormalizeForDetection(string input)
        => NormalizeInternal(input, applyLengthCap: false);

    /// <summary>
    /// Full normalization pipeline for user input before Gemini feed.
    /// Applies all transforms AND caps at 500 chars.
    /// </summary>
    public static string Normalize(string input) => NormalizeInternal(input, applyLengthCap: true);

    private static string NormalizeInternal(string input, bool applyLengthCap)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        // 1. NFKC: collapses fullwidth (ｉｇｎｏｒｅ → ignore), superscripts, ligatures, etc.
        var nfkc = input.Normalize(NormalizationForm.FormKC);

        // 2. Strip control chars + invisible markers + fold homoglyphs
        var sb = new StringBuilder(nfkc.Length);
        foreach (var c in nfkc)
        {
            if (char.IsControl(c)) continue;
            if (Array.IndexOf(InvisibleChars, c) >= 0) continue;
            sb.Append(HomoglyphMap.TryGetValue(c, out var fold) ? fold : c);
        }

        // 3. Strip LLM control tokens (case-insensitive)
        var text = sb.ToString();
        foreach (var token in LlmControlTokens)
            text = text.Replace(token, " ", StringComparison.OrdinalIgnoreCase);

        // 4. Collapse whitespace runs, trim
        text = WhitespaceRun.Replace(text, " ").Trim();

        // 5. Hard cap (only when feeding to Gemini — not for detection)
        if (applyLengthCap && text.Length > MaxLength)
            text = text[..MaxLength];

        return text;
    }

    /// <summary>
    /// Produces a leetspeak-decoded variant for detection only.
    /// NOT for display or prompt feed — only for pattern matching.
    /// </summary>
    public static string DeobfuscateForDetection(string input)
    {
        return input.ToLowerInvariant()
            .Replace('0', 'o')
            .Replace('1', 'i')
            .Replace('3', 'e')
            .Replace('4', 'a')
            .Replace('5', 's')
            .Replace('7', 't')
            .Replace('@', 'a')
            .Replace('!', 'i')
            .Replace('$', 's')
            .Replace('|', 'i');
    }
}
