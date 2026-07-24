using LocalList.API.NET.Features.Chat.I18n;

namespace LocalList.API.NET.Features.Chat.Services;

// Slot merge/completeness logic and next-question routing for ChatAgentService: contradiction-
// aware MergeSlots, critical-slot accounting, tier-2 gating and the slot → question/quick-reply
// lookups. Logic is identical to the original single-file version; only its location changed.
public partial class ChatAgentService
{
    /// <summary>Merges new slot values into current slots. Returns true if any slot was contradicted.</summary>
    private static bool MergeSlots(ChatSlots current, ChatSlots incoming, bool acknowledge,
        string lang, out string? ackPrefix)
    {
        ackPrefix = null;
        bool contradiction = false;

        if (incoming.City != null)
        {
            if (acknowledge && current.City != null && !current.City.Equals(incoming.City, StringComparison.OrdinalIgnoreCase))
            { contradiction = true; ackPrefix = ChatStrings.SwitchedCity(lang, incoming.City); }
            current.City = incoming.City;
        }
        if (incoming.Days.HasValue)
        {
            if (acknowledge && current.Days.HasValue && current.Days != incoming.Days)
            { contradiction = true; ackPrefix = ChatStrings.SwitchedDays(lang, incoming.Days.Value); }
            current.Days = incoming.Days;
        }
        if (incoming.GroupType != null)
        {
            if (acknowledge && current.GroupType != null && current.GroupType != incoming.GroupType)
            { contradiction = true; ackPrefix = ChatStrings.SwitchedGroupType(lang, incoming.GroupType); }
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
            { contradiction = true; ackPrefix = ChatStrings.SwitchedBudget(lang, incoming.Budget); }
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

    private static short CountFilledCriticalSlots(ChatSlots slots)
    {
        short count = 0;
        if (!string.IsNullOrWhiteSpace(slots.City)) count++;
        if (slots.Days.HasValue) count++;
        if (!string.IsNullOrWhiteSpace(slots.GroupType)) count++;
        if (slots.Categories.Count > 0) count++;
        if (!string.IsNullOrWhiteSpace(slots.Budget)) count++;
        return count;
    }

    private static bool AreAllTier2Filled(ChatSlots slots)
        => slots.Pace != null && slots.Dietary.Count > 0 && slots.VibesPrimary != null;

    private static (string aiMessage, List<ChatQuickReply> chips) BuildTier2Question(ChatSlots slots, string lang)
        => (ChatStrings.Tier2Question(lang), ChatStrings.Tier2Chips(lang));

    private static string? MostUrgentMissing(ChatSlots slots)
    {
        if (string.IsNullOrWhiteSpace(slots.City)) return "city";
        if (!slots.Days.HasValue) return "days";
        if (string.IsNullOrWhiteSpace(slots.GroupType)) return "groupType";
        if (slots.Categories.Count == 0) return "categories";
        if (string.IsNullOrWhiteSpace(slots.Budget)) return "budget";
        return null;
    }

    private static string NextSlotQuestion(ChatSlots slots, string lang)
        => ChatStrings.NextSlotQuestion(MostUrgentMissing(slots) ?? string.Empty, lang);

    private static List<ChatQuickReply> QuickRepliesForSlot(string slot, string lang)
        => ChatStrings.QuickRepliesForSlot(slot, lang);
}
