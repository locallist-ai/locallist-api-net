using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.NET.Features.Chat.Services;

// Session lifecycle for ChatAgentService: load-or-create with ownership/IP checks and
// active-session archival, plus traveler-profile slot pre-fill for authenticated users.
// Logic is identical to the original single-file version; only its location changed.
public partial class ChatAgentService
{
    private async Task<ChatSession> LoadOrCreateSessionAsync(
        Guid? sessionId, Guid? userId, string? ipHash, CancellationToken ct)
    {
        if (sessionId.HasValue)
        {
            var existing = await _db.ChatSessions
                .FirstOrDefaultAsync(s => s.Id == sessionId.Value, ct);

            if (existing != null && existing.Status is "active" or "ready" or "quarantined")
            {
                // L3: anon session ownership — verify IP hasn't changed
                if (existing.UserId == null && ipHash != null &&
                    existing.AnonymousIpHash != null &&
                    !existing.AnonymousIpHash.Equals(ipHash, StringComparison.Ordinal))
                {
                    _secLog.AnonSessionMismatch(existing.Id);
                    // Fall through to create a new session (do NOT continue existing)
                }
                else
                {
                    return existing;
                }
            }
        }

        // Archive oldest active session if user has too many
        if (userId.HasValue)
        {
            var activeSessions = await _db.ChatSessions
                .Where(s => s.UserId == userId && s.Status == "active")
                .OrderBy(s => s.LastTurnAt)
                .ToListAsync(ct);

            if (activeSessions.Count >= MaxActiveSessions)
            {
                var toArchive = activeSessions.First();
                toArchive.Status = "abandoned";
                _db.Update(toArchive);
            }
        }

        // Pre-fill slots from saved traveler profile for authenticated users
        var prefilledSlots = userId.HasValue
            ? await PreFillSlotsFromProfileAsync(userId.Value, ct)
            : null;

        var session = new ChatSession
        {
            UserId = userId,
            AnonymousIpHash = userId.HasValue ? null : ipHash,
            SlotsJson = prefilledSlots != null
                ? JsonSerializer.Serialize(prefilledSlots)
                : "{}",
        };
        _db.ChatSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Chat: new session={Session} userId={UserId} profilePrefilled={Prefilled}",
            session.Id, userId?.ToString() ?? "anon", prefilledSlots != null);

        var sessionDistinctId = userId?.ToString() ?? ipHash ?? session.Id.ToString();
        _ = _posthog.CaptureAsync(sessionDistinctId, "chat_session_started", new()
        {
            ["session_id"] = session.Id.ToString(),
            ["authenticated"] = userId.HasValue,
        });

        return session;
    }

    private async Task<ChatSlots?> PreFillSlotsFromProfileAsync(Guid userId, CancellationToken ct)
    {
        var profile = await _db.UserProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile == null) return null;

        var slots = new ChatSlots();
        bool anyPrefilled = false;

        if (!string.IsNullOrWhiteSpace(profile.DefaultGroupType))
        {
            slots.GroupType = profile.DefaultGroupType;
            anyPrefilled = true;
        }
        if (profile.DietaryRestrictions.Count > 0)
        {
            slots.Dietary = new List<string>(profile.DietaryRestrictions);
            anyPrefilled = true;
        }
        if (!string.IsNullOrWhiteSpace(profile.PacePreference))
        {
            slots.Pace = profile.PacePreference;
            anyPrefilled = true;
        }
        if (!string.IsNullOrWhiteSpace(profile.DefaultBudgetTier))
        {
            slots.Budget = profile.DefaultBudgetTier;
            anyPrefilled = true;
        }
        if (!string.IsNullOrWhiteSpace(profile.FavoriteCity))
        {
            slots.City = profile.FavoriteCity;
            anyPrefilled = true;
        }

        return anyPrefilled ? slots : null;
    }
}
