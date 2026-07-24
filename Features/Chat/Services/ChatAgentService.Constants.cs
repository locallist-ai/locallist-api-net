namespace LocalList.API.NET.Features.Chat.Services;

// Tunables and lookup tables for ChatAgentService: turn/session caps, the deterministic
// quick-reply → slot map and the universal-chip allowlist. Values and comments are identical
// to the original single-file version; only their location changed.
public partial class ChatAgentService
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
}
