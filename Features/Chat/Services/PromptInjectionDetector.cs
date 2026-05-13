namespace LocalList.API.NET.Features.Chat.Services;

/// <summary>
/// Compatibility shim — delegates to JailbreakPatternLibrary (PR 1.5).
/// Kept for test references; do not add new logic here.
/// </summary>
public static class PromptInjectionDetector
{
    public static bool IsInjection(string input)
        => JailbreakPatternLibrary.IsInjection(InputNormalizer.NormalizeForDetection(input));

    public static bool IsOffTopic(string input)
        => JailbreakPatternLibrary.IsOffTopic(InputNormalizer.NormalizeForDetection(input));
}
