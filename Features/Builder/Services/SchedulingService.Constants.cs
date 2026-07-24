namespace LocalList.API.NET.Features.Builder.Services;

// Tunables, lookup tables and viability-gate constants for SchedulingService. Values and
// comments are identical to the original single-file version; only their location changed.
public partial class SchedulingService
{
    // ── Time-block compatibility (kept for IsGoodTimeMatch) ───────────────────

    private static readonly Dictionary<string, HashSet<string>> TimeBlockCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["morning"]   = new(StringComparer.OrdinalIgnoreCase) { "coffee", "wellness", "outdoors", "culture", "food" },
        ["lunch"]     = new(StringComparer.OrdinalIgnoreCase) { "food", "coffee" },
        ["afternoon"] = new(StringComparer.OrdinalIgnoreCase) { "coffee", "outdoors", "culture", "food" },
        ["dinner"]    = new(StringComparer.OrdinalIgnoreCase) { "food" },
        ["evening"]   = new(StringComparer.OrdinalIgnoreCase) { "nightlife", "food", "culture" },
    };

    private static readonly Dictionary<string, string[]> BestTimeMatches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["morning"]   = new[] { "morning" },
        ["lunch"]     = new[] { "lunch", "morning", "afternoon" },
        ["afternoon"] = new[] { "afternoon", "morning" },
        ["dinner"]    = new[] { "dinner", "evening", "lunch" },
        ["evening"]   = new[] { "evening" },
    };

    // ── Visit durations by category ───────────────────────────────────────────

    private static readonly Dictionary<string, int> CategoryDurationMin = new(StringComparer.OrdinalIgnoreCase)
    {
        ["coffee"]        = 45,
        ["food"]          = 90,
        ["culture"]       = 120,
        ["outdoors"]      = 75,
        ["wellness"]      = 90,
        ["nightlife"]     = 120,
        ["shopping"]      = 60,
        ["entertainment"] = 105,
    };
    private const int DefaultDurationMin = 75;

    // ── Day scheduling constants ──────────────────────────────────────────────

    private static readonly TimeSpan DayStart       = TimeSpan.FromHours(9.5);   // 09:30
    private static readonly TimeSpan DaySoftCap     = TimeSpan.FromHours(22);    // 22:00 → warning day_overpacked
    private static readonly TimeSpan LunchIdeal     = TimeSpan.FromHours(13);    // 13:00
    private static readonly TimeSpan LunchWinStart  = TimeSpan.FromHours(11.5);  // 11:30
    private static readonly TimeSpan LunchWinEnd    = TimeSpan.FromHours(14.5);  // 14:30
    private static readonly TimeSpan DinnerIdeal    = TimeSpan.FromHours(19.5);  // 19:30
    private static readonly TimeSpan DinnerWinStart = TimeSpan.FromHours(18);    // 18:00
    private static readonly TimeSpan DinnerWinEnd   = TimeSpan.FromHours(21.5);  // 21:30

    // ── Viability gate constants (API-2) ───────────────────────────────────────
    // Tunables: single edit here, no refactor. Kept as named constants so Pablo can
    // adjust the viability envelope (dead-gap tolerance, hard day cap, per-leg travel
    // ceiling) without touching the walk logic. Promote to IOptions if per-city/per-tier
    // tuning is ever needed.

    /// <summary>M4: max time the clock may jump forward waiting for a place to open before
    /// the gap is judged "dead" and the candidate is skipped instead of waited out.</summary>
    private static readonly TimeSpan MaxWaitForOpen  = TimeSpan.FromMinutes(90);

    /// <summary>M3: hard end-of-day cap. Any arrival past this truncates the day (break).
    /// Applies to every stop except nightlife, which gets the later <see cref="NightlifeHardCap"/>.</summary>
    private static readonly TimeSpan DayHardCap      = new(23, 0, 0);   // 23:00
    private static readonly TimeSpan NightlifeHardCap = new(23, 59, 0); // 23:59 — nightlife exception

    /// <summary>m8: per-leg travel ceiling. A single travel leg longer than this marks the
    /// candidate as geographically implausible (Roamy 8h/220mi class bug) and it is skipped.</summary>
    private const int MaxLegTravelMin = 60;
}
