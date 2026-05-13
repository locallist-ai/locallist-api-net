namespace LocalList.API.NET.Features.Chat.Services;

/// <summary>
/// Compatibility shim — delegates to OutputValidator (PR 1.5).
/// Kept for test references; do not add new logic here.
/// </summary>
public static class ResponseDriftDetector
{
    public static bool HasDrift(string aiMessage)
        => OutputValidator.HasDrift(aiMessage);
}
