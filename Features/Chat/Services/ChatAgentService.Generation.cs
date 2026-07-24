using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.NET.Features.Chat.Services;

// Generate-flow helpers used by ChatController.Generate: ownership-checked session lookup,
// slot deserialization, slots → TripContextDto projection and the natural-language slot
// summary. Logic is identical to the original single-file version; only its location changed.
public partial class ChatAgentService
{
    /// <summary>
    /// Loads a session and verifies ownership (userId or IP-hash).
    /// Returns null with an error code if not found, forbidden, or quarantined.
    /// </summary>
    public async Task<(ChatSession? Session, string? Error)> FindSessionForGenerationAsync(
        Guid sessionId, Guid? userId, string? rawIp, CancellationToken ct)
    {
        var ipHash = HashIp(rawIp);
        var session = await _db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session == null) return (null, "session_not_found");

        // Authenticated user must own the session
        if (session.UserId.HasValue && session.UserId != userId)
            return (null, "forbidden");

        // Anonymous session must match originating IP
        if (!session.UserId.HasValue &&
            !string.IsNullOrEmpty(session.AnonymousIpHash) &&
            !string.IsNullOrEmpty(ipHash) &&
            !session.AnonymousIpHash.Equals(ipHash, StringComparison.Ordinal))
            return (null, "forbidden");

        if (session.Status == "quarantined") return (null, "session_quarantined");

        return (session, null);
    }

    public static ChatSlots GetSlots(ChatSession session) => DeserializeSlots(session.SlotsJson);

    /// <summary>Converts the filled slots into a TripContextDto for plan generation.</summary>
    public static TripContextDto SlotsToTripContext(ChatSlots slots) => new()
    {
        City = slots.City,
        Days = slots.Days,
        GroupType = slots.GroupType,
        // El tier fluye nativo: MergeContextIntoPrefs mapea context.Budget →
        // prefs.BudgetTier y ScoreBudgetMatch puntúa por banda (premium → $$$/$$$$).
        // El antiguo mapeo tier→amount aquí forzaba banda de un solo tier y
        // penalizaba $$$$ con premium (0.6 en vez de 1.0).
        Budget = slots.Budget,
        Categories = slots.Categories.Count > 0 ? slots.Categories : null,
        Pace = slots.Pace,
        Dietary = slots.Dietary.Count > 0 ? slots.Dietary : null,
        Exclusions = slots.Exclusions.Count > 0 ? slots.Exclusions : null,
        VibesPrimary = slots.VibesPrimary,
    };

    /// <summary>
    /// Builds a natural-language summary of filled slots to pass as the "message"
    /// to ExtractPreferencesAsync, so Gemini picks up dietary/pace/exclusions/vibes.
    /// </summary>
    public static string BuildSummaryMessage(ChatSlots slots)
    {
        var parts = new List<string>();
        if (slots.Days.HasValue && !string.IsNullOrEmpty(slots.City))
            parts.Add($"{slots.Days}-day trip in {slots.City}");
        else if (!string.IsNullOrEmpty(slots.City))
            parts.Add($"trip in {slots.City}");
        if (!string.IsNullOrEmpty(slots.GroupType))
            parts.Add($"group: {slots.GroupType}");
        if (slots.Categories.Count > 0)
            parts.Add($"interests: {string.Join(", ", slots.Categories)}");
        if (!string.IsNullOrEmpty(slots.VibesPrimary))
            parts.Add($"vibe: {slots.VibesPrimary}");
        if (slots.Dietary.Count > 0 && !slots.Dietary.Contains("none", StringComparer.OrdinalIgnoreCase))
            parts.Add($"dietary: {string.Join(", ", slots.Dietary)}");
        if (!string.IsNullOrEmpty(slots.Pace))
            parts.Add($"pace: {slots.Pace}");
        if (slots.Exclusions.Count > 0)
            parts.Add($"avoid: {string.Join(", ", slots.Exclusions)}");
        return string.Join(". ", parts);
    }
}
