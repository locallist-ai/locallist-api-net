using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalList.API.NET.Features.Builder;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.PostHog;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.NET.Features.Chat.Services;

public class ChatAgentService
{
    private const int TurnLimit = 6;
    private const int MaxActiveSessions = 3;

    private static readonly Dictionary<string, ChatSlots> QuickReplyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Budget
        ["budget_budget"]   = new ChatSlots { Budget = "budget" },
        ["budget_moderate"] = new ChatSlots { Budget = "moderate" },
        ["budget_premium"]  = new ChatSlots { Budget = "premium" },
        // Pace
        ["pace_slow"]   = new ChatSlots { Pace = "slow" },
        ["pace_normal"] = new ChatSlots { Pace = "normal" },
        ["pace_fast"]   = new ChatSlots { Pace = "fast" },
        // GroupType
        ["group_solo"]        = new ChatSlots { GroupType = "solo" },
        ["group_couple"]      = new ChatSlots { GroupType = "couple" },
        ["group_friends"]     = new ChatSlots { GroupType = "friends" },
        ["group_family"]      = new ChatSlots { GroupType = "family" },
        ["group_family_kids"] = new ChatSlots { GroupType = "family-kids" },
        // Dietary
        ["diet_vegetarian"] = new ChatSlots { Dietary = ["vegetarian"] },
        ["diet_vegan"]      = new ChatSlots { Dietary = ["vegan"] },
        ["diet_halal"]      = new ChatSlots { Dietary = ["halal"] },
        ["diet_kosher"]     = new ChatSlots { Dietary = ["kosher"] },
        ["diet_glutenfree"] = new ChatSlots { Dietary = ["gluten-free"] },
        ["diet_none"]       = new ChatSlots { Dietary = ["none"] },
        // Days
        ["days_1"] = new ChatSlots { Days = 1 },
        ["days_2"] = new ChatSlots { Days = 2 },
        ["days_3"] = new ChatSlots { Days = 3 },
        ["days_4"] = new ChatSlots { Days = 4 },
        ["days_7"] = new ChatSlots { Days = 7 },
        // Vibes
        ["vibe_romantic"]    = new ChatSlots { VibesPrimary = "romantic" },
        ["vibe_adventurous"] = new ChatSlots { VibesPrimary = "adventurous" },
        ["vibe_hidden_gems"] = new ChatSlots { VibesPrimary = "hidden_gems" },
        ["vibe_cultural"]    = new ChatSlots { VibesPrimary = "cultural" },
        ["vibe_foodie"]      = new ChatSlots { VibesPrimary = "foodie" },
        ["vibe_relaxed"]     = new ChatSlots { VibesPrimary = "relaxed" },
        // Tier 2 skip
        ["skip_refinements"] = new ChatSlots(),
    };

    // Chips that are always valid regardless of what was offered last turn
    private static readonly HashSet<string> UniversalChips = new(StringComparer.OrdinalIgnoreCase)
    {
        "skip_refinements"
    };

    private readonly LocalListDbContext _db;
    private readonly SlotExtractorService _extractor;
    private readonly ILogger<ChatAgentService> _logger;
    private readonly ChatSecLogger _secLog;
    private readonly IConfiguration _config;
    private readonly PostHogService _posthog;

    public ChatAgentService(
        LocalListDbContext db,
        SlotExtractorService extractor,
        ILogger<ChatAgentService> logger,
        ChatSecLogger secLog,
        IConfiguration config,
        PostHogService posthog)
    {
        _db = db;
        _extractor = extractor;
        _logger = logger;
        _secLog = secLog;
        _config = config;
        _posthog = posthog;
    }

    public async Task<ChatTurnResponse> ProcessTurnAsync(
        Guid? sessionId,
        string? rawMessage,
        string? quickReplyId,
        PreSeededSlots? preSeededSlots,
        Guid? userId,
        string? rawIp,
        CancellationToken ct = default)
    {
        var ipHash = HashIp(rawIp);
        var session = await LoadOrCreateSessionAsync(sessionId, userId, ipHash, ct);
        var suspicion = DeserializeSuspicion(session.SuspicionJson);

        // If already quarantined, refuse immediately
        if (session.Status == "quarantined")
            return BuildQuarantineResponse(session, DeserializeSlots(session.SlotsJson));

        var slots = DeserializeSlots(session.SlotsJson);
        var history = DeserializeHistory(session.HistoryJson);

        // ── PreSeededSlots: honor client-supplied city on brand-new sessions only ──
        if (session.TurnCount == 0 && !string.IsNullOrWhiteSpace(preSeededSlots?.City))
        {
            var normalizedCity = preSeededSlots.City.Trim().ToLowerInvariant();
            var cityExists = await _db.Cities.AnyAsync(c => c.NormalizedName == normalizedCity, ct);
            if (cityExists && string.IsNullOrEmpty(slots.City))
            {
                slots.City = preSeededSlots.City.Trim();
                _logger.LogInformation("Chat: preSeeded city={City} sessionId={Session}", slots.City, session.Id);
            }

            // If client sent no message (city-only first turn), return greeting immediately
            if (string.IsNullOrWhiteSpace(rawMessage) && string.IsNullOrWhiteSpace(quickReplyId))
            {
                var greeting = string.IsNullOrEmpty(slots.City)
                    ? "What city are you visiting?"
                    : $"Great — let's plan your {slots.City} trip! How many days?";
                var greetingReplies = new List<ChatQuickReply>
                {
                    new() { Id = "days_1", Label = "1 day" },
                    new() { Id = "days_2", Label = "2 days" },
                    new() { Id = "days_3", Label = "3 days" },
                    new() { Id = "days_4", Label = "4 days" },
                    new() { Id = "days_7", Label = "1 week" },
                };
                history.Add(new HistoryEntry { Role = "assistant", Content = greeting, Timestamp = DateTimeOffset.UtcNow });
                return await BuildResponseAsync(session, slots, history, suspicion, greeting, ct,
                    geminiQuickReplies: greetingReplies);
            }
        }

        // ── Quick reply: deterministic slot update, no Gemini call ──
        if (!string.IsNullOrWhiteSpace(quickReplyId))
        {
            // L3 anti-forgery: chip must have been offered last turn (or be universal)
            if (!UniversalChips.Contains(quickReplyId) &&
                !session.LastOfferedChips.Contains(quickReplyId, StringComparer.OrdinalIgnoreCase))
            {
                _secLog.ChipForgeryAttempt(session.Id, quickReplyId);
                return new ChatTurnResponse
                {
                    SessionId = session.Id,
                    AiMessage = "Please select one of the available options.",
                    Slots = slots,
                    MissingCritical = GetMissingCritical(slots),
                    QuickReplies = new(),
                    Ready = false,
                    TurnCount = session.TurnCount,
                    TurnLimit = TurnLimit
                };
            }

            if (QuickReplyMap.TryGetValue(quickReplyId, out var chipSlots))
            {
                MergeSlots(slots, chipSlots, acknowledge: false, out _);
                _logger.LogInformation("Chat: quickReply={Id} sessionId={Session}", quickReplyId, session.Id);
                history.Add(new HistoryEntry
                {
                    Role = "user",
                    Content = $"[chip: {quickReplyId}]",
                    Timestamp = DateTimeOffset.UtcNow
                });
                suspicion.RecordCleanTurn();
                return await BuildResponseAsync(session, slots, history, suspicion, aiMessage: null, ct);
            }
        }

        // ── Free text ──
        // L2: normalize for detection (full input, no cap — we need to see all of it)
        var messageForDetection = InputNormalizer.NormalizeForDetection(rawMessage ?? string.Empty);
        // L2: normalize for Gemini feed (capped at 500 chars)
        var message = InputNormalizer.Normalize(rawMessage ?? string.Empty);

        // L3: injection check against full normalized input (not capped)
        if (JailbreakPatternLibrary.IsInjection(messageForDetection))
        {
            suspicion.RecordInjection("pattern_match");
            _secLog.InjectionDetected(session.Id, suspicion.LastTrigger ?? "unknown", suspicion.Score);

            if (suspicion.ShouldQuarantine)
                return await QuarantineSessionAsync(session, slots, suspicion, ct, "injection");

            session.SuspicionJson = JsonSerializer.Serialize(suspicion);
            _db.Update(session);
            await _db.SaveChangesAsync(ct);

            return BuildCannedResponse(session, slots,
                "I can only help plan your trip. Where are you headed?",
                nextSlot: MostUrgentMissing(slots));
        }

        if (JailbreakPatternLibrary.IsOffTopic(messageForDetection))
        {
            suspicion.RecordOffTopic();
            _secLog.OffTopicDetected(session.Id, suspicion.Score);

            session.SuspicionJson = JsonSerializer.Serialize(suspicion);
            _db.Update(session);
            await _db.SaveChangesAsync(ct);

            return BuildCannedResponse(session, slots,
                $"Let's focus on your trip — {NextSlotQuestion(slots)}",
                nextSlot: MostUrgentMissing(slots));
        }

        history.Add(new HistoryEntry { Role = "user", Content = message, Timestamp = DateTimeOffset.UtcNow });

        // Turn cap
        if (session.TurnCount >= TurnLimit - 1)
        {
            _logger.LogInformation("Chat: turn cap reached sessionId={Session}", session.Id);
            return await BuildResponseAsync(session, slots, history, suspicion,
                aiMessage: "Ready to build your plan!", ct);
        }

        // L3 suspicion: if score ≥ 50, skip Gemini to avoid burning tokens on adversarial sessions
        string aiMessage;
        SlotExtractorResult? extracted = null;

        if (suspicion.ShouldSuppressGemini)
        {
            aiMessage = $"Let's focus on your trip — {NextSlotQuestion(slots)}";
        }
        else
        {
            extracted = await _extractor.ExtractAsync(message, slots, history, ct);

            if (extracted == null)
            {
                aiMessage = SlotExtractorService.ParseFallback;
            }
            else
            {
                bool hasContradiction = MergeSlots(slots, extracted.Extracted, acknowledge: true, out string? ackPrefix);

                // L5: validate output
                var drift = OutputValidator.Inspect(extracted.AiMessage);
                if (drift == OutputValidator.DriftKind.CanaryLeak)
                {
                    suspicion.RecordCanaryLeak();
                    _secLog.CanaryLeak(session.Id);
                    return await QuarantineSessionAsync(session, slots, suspicion, ct, "canary_leak");
                }
                else if (drift != OutputValidator.DriftKind.None)
                {
                    suspicion.RecordDrift(drift.ToString());
                    _secLog.DriftDetected(session.Id, drift.ToString(), suspicion.Score);
                    aiMessage = $"Got it! {NextSlotQuestion(slots)}";
                }
                else
                {
                    aiMessage = (hasContradiction && ackPrefix != null)
                        ? $"{ackPrefix} {extracted.AiMessage}"
                        : extracted.AiMessage;
                }
            }
        }

        suspicion.RecordCleanTurn();
        history.Add(new HistoryEntry { Role = "assistant", Content = aiMessage, Timestamp = DateTimeOffset.UtcNow });

        return await BuildResponseAsync(session, slots, history, suspicion, aiMessage, ct,
            geminiQuickReplies: extracted?.QuickReplies);
    }

    // ── Private helpers ──

    private async Task<ChatTurnResponse> BuildResponseAsync(
        ChatSession session,
        ChatSlots slots,
        List<HistoryEntry> history,
        SuspicionTracker suspicion,
        string? aiMessage,
        CancellationToken ct,
        List<ChatQuickReply>? geminiQuickReplies = null)
    {
        var missing = GetMissingCritical(slots);
        var ready = missing.Count == 0;

        List<ChatQuickReply> quickReplies = geminiQuickReplies ?? new();

        if (ready && !AreAllTier2Filled(slots) && quickReplies.Count == 0)
        {
            (aiMessage, quickReplies) = BuildTier2Question(slots);
            ready = false;
        }
        else if (ready && quickReplies.Count == 0)
        {
            aiMessage ??= "Ready to build your plan!";
        }

        if (session.TurnCount + 1 >= TurnLimit) ready = true;

        // L6: sanitize aiMessage before persisting and returning
        aiMessage = OutputSanitizer.Sanitize(aiMessage ?? "What city are you visiting?");

        var wasAlreadyReady = session.Status == "ready";
        session.TurnCount++;
        session.LastTurnAt = DateTimeOffset.UtcNow;
        session.Status = ready ? "ready" : "active";
        session.SlotsJson = JsonSerializer.Serialize(slots);
        session.SuspicionJson = JsonSerializer.Serialize(suspicion);
        session.LastOfferedChips = quickReplies.Select(qr => qr.Id).ToArray();

        var trimmedHistory = history.TakeLast(20).ToList();
        session.HistoryJson = JsonSerializer.Serialize(trimmedHistory);

        _db.Update(session);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Chat: turn={Turn} sessionId={Session} missing=[{Missing}] ready={Ready} suspicion={Score}",
            session.TurnCount, session.Id, string.Join(",", missing), ready, suspicion.Score);

        if (ready && !wasAlreadyReady)
        {
            var readyDistinctId = session.UserId?.ToString() ?? session.AnonymousIpHash ?? session.Id.ToString();
            _ = _posthog.CaptureAsync(readyDistinctId, "chat_ready", new()
            {
                ["session_id"] = session.Id.ToString(),
                ["turn_count"] = session.TurnCount,
                ["city"] = slots.City,
                ["days"] = (object?)slots.Days,
            });
        }

        return new ChatTurnResponse
        {
            SessionId = session.Id,
            AiMessage = aiMessage,
            Slots = slots,
            MissingCritical = missing,
            QuickReplies = quickReplies.Take(4).ToList(),
            Ready = ready,
            TurnCount = session.TurnCount,
            TurnLimit = TurnLimit
        };
    }

    private ChatTurnResponse BuildCannedResponse(
        ChatSession session, ChatSlots slots, string message, string? nextSlot)
    {
        return new ChatTurnResponse
        {
            SessionId = session.Id,
            AiMessage = OutputSanitizer.Sanitize(message),
            Slots = slots,
            MissingCritical = GetMissingCritical(slots),
            QuickReplies = nextSlot != null ? QuickRepliesForSlot(nextSlot) : new(),
            Ready = false,
            TurnCount = session.TurnCount,
            TurnLimit = TurnLimit
        };
    }

    private ChatTurnResponse BuildQuarantineResponse(ChatSession session, ChatSlots slots)
    {
        return new ChatTurnResponse
        {
            SessionId = session.Id,
            AiMessage = "This conversation was reset for safety. Please try the wizard instead.",
            Slots = slots,
            MissingCritical = GetMissingCritical(slots),
            QuickReplies = new(),
            Ready = false,
            TurnCount = session.TurnCount,
            TurnLimit = TurnLimit
        };
    }

    private async Task<ChatTurnResponse> QuarantineSessionAsync(
        ChatSession session, ChatSlots slots, SuspicionTracker suspicion,
        CancellationToken ct, string reason)
    {
        suspicion.LastTrigger = reason;
        _secLog.Quarantined(session.Id, reason, suspicion.Score);

        session.Status = "quarantined";
        session.SuspicionJson = JsonSerializer.Serialize(suspicion);
        _db.Update(session);
        await _db.SaveChangesAsync(ct);

        return BuildQuarantineResponse(session, slots);
    }

    private async Task<ChatSession> LoadOrCreateSessionAsync(
        Guid? sessionId, Guid? userId, string? ipHash, CancellationToken ct)
    {
        if (sessionId.HasValue)
        {
            var existing = await _db.ChatSessions
                .FirstOrDefaultAsync(s => s.Id == sessionId.Value, ct);

            if (existing != null && existing.Status is "active" or "ready")
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

    /// <summary>Merges new slot values into current slots. Returns true if any slot was contradicted.</summary>
    private static bool MergeSlots(ChatSlots current, ChatSlots incoming, bool acknowledge,
        out string? ackPrefix)
    {
        ackPrefix = null;
        bool contradiction = false;

        if (incoming.City != null)
        {
            if (acknowledge && current.City != null && !current.City.Equals(incoming.City, StringComparison.OrdinalIgnoreCase))
            { contradiction = true; ackPrefix = $"Switched city to {incoming.City}."; }
            current.City = incoming.City;
        }
        if (incoming.Days.HasValue)
        {
            if (acknowledge && current.Days.HasValue && current.Days != incoming.Days)
            { contradiction = true; ackPrefix = $"Switched to {incoming.Days} days."; }
            current.Days = incoming.Days;
        }
        if (incoming.GroupType != null)
        {
            if (acknowledge && current.GroupType != null && current.GroupType != incoming.GroupType)
            { contradiction = true; ackPrefix = $"Switched to {incoming.GroupType}."; }
            current.GroupType = incoming.GroupType;
        }
        if (incoming.Categories.Count > 0)
        {
            foreach (var cat in incoming.Categories)
                if (!current.Categories.Contains(cat, StringComparer.OrdinalIgnoreCase))
                    current.Categories.Add(cat);
        }
        if (incoming.Budget != null)
        {
            if (acknowledge && current.Budget != null && current.Budget != incoming.Budget)
            { contradiction = true; ackPrefix = $"Switched budget to {incoming.Budget}."; }
            current.Budget = incoming.Budget;
        }
        if (incoming.Pace != null) current.Pace = incoming.Pace;
        if (incoming.Dietary.Count > 0) current.Dietary = incoming.Dietary;
        if (incoming.Exclusions.Count > 0)
        {
            foreach (var ex in incoming.Exclusions)
                if (!current.Exclusions.Contains(ex, StringComparer.OrdinalIgnoreCase))
                    current.Exclusions.Add(ex);
        }
        if (incoming.VibesPrimary != null) current.VibesPrimary = incoming.VibesPrimary;
        if (incoming.AccommodationArea != null) current.AccommodationArea = incoming.AccommodationArea;
        if (incoming.Mobility != null) current.Mobility = incoming.Mobility;
        if (incoming.TimeOfDay != null) current.TimeOfDay = incoming.TimeOfDay;

        return contradiction;
    }

    private static List<string> GetMissingCritical(ChatSlots slots)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(slots.City)) missing.Add("city");
        if (!slots.Days.HasValue) missing.Add("days");
        if (string.IsNullOrWhiteSpace(slots.GroupType)) missing.Add("groupType");
        if (slots.Categories.Count == 0) missing.Add("categories");
        if (string.IsNullOrWhiteSpace(slots.Budget)) missing.Add("budget");
        return missing;
    }

    private static bool AreAllTier2Filled(ChatSlots slots)
        => slots.Pace != null && slots.Dietary.Count > 0 && slots.VibesPrimary != null;

    private static (string aiMessage, List<ChatQuickReply> chips) BuildTier2Question(ChatSlots slots)
    {
        var msg = "One last touch — any dietary restrictions, and what pace?";
        var chips = new List<ChatQuickReply>
        {
            new() { Id = "diet_vegetarian", Label = "🥗 Vegetarian" },
            new() { Id = "diet_none", Label = "✅ No restrictions" },
            new() { Id = "pace_slow", Label = "🐢 Slow pace" },
            new() { Id = "skip_refinements", Label = "⏭️ Skip" },
        };
        return (msg, chips);
    }

    private static string? MostUrgentMissing(ChatSlots slots)
    {
        if (string.IsNullOrWhiteSpace(slots.City)) return "city";
        if (!slots.Days.HasValue) return "days";
        if (string.IsNullOrWhiteSpace(slots.GroupType)) return "groupType";
        if (slots.Categories.Count == 0) return "categories";
        if (string.IsNullOrWhiteSpace(slots.Budget)) return "budget";
        return null;
    }

    private static string NextSlotQuestion(ChatSlots slots)
    {
        var urgent = MostUrgentMissing(slots);
        return urgent switch
        {
            "city"      => "Where are you headed?",
            "days"      => "How many days?",
            "groupType" => "Who are you travelling with?",
            "categories"=> "What do you enjoy? (food, culture, outdoors...)",
            "budget"    => "What's your budget vibe?",
            _           => "Ready to build!"
        };
    }

    private static List<ChatQuickReply> QuickRepliesForSlot(string slot) => slot switch
    {
        "budget" => new List<ChatQuickReply>
        {
            new() { Id = "budget_budget",   Label = "💸 Tight" },
            new() { Id = "budget_moderate", Label = "🍽️ Comfortable" },
            new() { Id = "budget_premium",  Label = "✨ Splurge" },
        },
        "groupType" => new List<ChatQuickReply>
        {
            new() { Id = "group_solo",    Label = "🧍 Solo" },
            new() { Id = "group_couple",  Label = "👫 Couple" },
            new() { Id = "group_friends", Label = "👯 Friends" },
            new() { Id = "group_family",  Label = "👨‍👩‍👧 Family" },
        },
        "days" => new List<ChatQuickReply>
        {
            new() { Id = "days_1", Label = "1 day" },
            new() { Id = "days_2", Label = "2 days" },
            new() { Id = "days_3", Label = "3 days" },
            new() { Id = "days_4", Label = "4+ days" },
        },
        _ => new()
    };

    // ── Generate-flow helpers (used by ChatController.Generate) ──────────────

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

    private string? HashIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var salt = _config["WAITLIST_IP_SALT"];
        if (string.IsNullOrEmpty(salt))
        {
            _logger.LogWarning("Chat: WAITLIST_IP_SALT not configured — IP hashing uses fallback salt");
            salt = "chat-fallback-salt-set-WAITLIST_IP_SALT-env";
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ip + salt));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static ChatSlots DeserializeSlots(string json)
    {
        try { return JsonSerializer.Deserialize<ChatSlots>(json) ?? new(); }
        catch { return new(); }
    }

    private static List<HistoryEntry> DeserializeHistory(string json)
    {
        try { return JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new(); }
        catch { return new(); }
    }

    private static SuspicionTracker DeserializeSuspicion(string json)
    {
        try { return JsonSerializer.Deserialize<SuspicionTracker>(json) ?? new(); }
        catch { return new(); }
    }
}
