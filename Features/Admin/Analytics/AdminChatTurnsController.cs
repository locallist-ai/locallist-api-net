using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Features.Admin.Analytics;

[ApiController]
[Route("admin/analytics/chat-turns")]
[AdminAuthorize]
[EnableRateLimiting("AdminLimit")]
public class AdminChatTurnsController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<AdminChatTurnsController> _logger;

    public AdminChatTurnsController(LocalListDbContext db, ILogger<AdminChatTurnsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Paginated list of chat turns with optional filters.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] Guid? sessionId = null,
        [FromQuery] Guid? userId = null,
        [FromQuery] bool? hasError = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);

        var query = _db.ChatTurns.AsNoTracking();

        if (sessionId.HasValue) query = query.Where(t => t.SessionId == sessionId.Value);
        if (userId.HasValue)    query = query.Where(t => t.UserId == userId.Value);
        if (hasError == true)   query = query.Where(t => t.ErrorCode != null);
        if (hasError == false)  query = query.Where(t => t.ErrorCode == null);
        if (from.HasValue)      query = query.Where(t => t.CreatedAt >= from.Value);
        if (to.HasValue)        query = query.Where(t => t.CreatedAt <= to.Value);

        var total = await query.CountAsync(ct);
        var turns = await query
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id) // tiebreaker: orden total y estable con CreatedAt empatado (paginación sin dups/omisiones)
            .Skip(offset)
            .Take(limit)
            .Select(t => new AdminChatTurnDto(
                t.Id, t.CreatedAt, t.SessionId, t.UserId, t.TurnIndex,
                t.AiProvider, t.Model, t.PromptVersion, t.PromptChars,
                t.FinishReason, t.LatencyMs, t.InputTokens, t.OutputTokens,
                t.ThinkingTokens, t.TotalTokens, t.CostUsd, t.GeminiStatus,
                t.ErrorCode, t.ErrorMessage, t.SlotCompleteness))
            .ToListAsync(ct);

        return Ok(new AdminChatTurnsListResponse(turns, total, limit, offset));
    }

    /// <summary>Aggregated stats across all chat turns in the optional date range.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var query = _db.ChatTurns.AsNoTracking();
        if (from.HasValue) query = query.Where(t => t.CreatedAt >= from.Value);
        if (to.HasValue)   query = query.Where(t => t.CreatedAt <= to.Value);

        var turns = await query
            .Select(t => new
            {
                t.LatencyMs, t.InputTokens, t.OutputTokens, t.ThinkingTokens,
                t.CostUsd, t.SlotCompleteness, t.ErrorCode, t.FinishReason
            })
            .ToListAsync(ct);

        if (turns.Count == 0)
        {
            return Ok(new AdminChatTurnsStatsDto(
                0, 0, 0, 0, 0, 0, 0, 0,
                new Dictionary<string, int>(),
                new Dictionary<string, int>()));
        }

        var errorCount = turns.Count(t => t.ErrorCode != null);
        var withSlot = turns.Where(t => t.SlotCompleteness.HasValue).ToList();

        var finishBreakdown = turns
            .Where(t => t.FinishReason != null)
            .GroupBy(t => t.FinishReason!)
            .ToDictionary(g => g.Key, g => g.Count());

        var errorBreakdown = turns
            .Where(t => t.ErrorCode != null)
            .GroupBy(t => t.ErrorCode!)
            .ToDictionary(g => g.Key, g => g.Count());

        return Ok(new AdminChatTurnsStatsDto(
            TotalTurns: turns.Count,
            AvgLatencyMs: turns.Average(t => (double)t.LatencyMs),
            TotalInputTokens: turns.Sum(t => (long)(t.InputTokens ?? 0)),
            TotalOutputTokens: turns.Sum(t => (long)(t.OutputTokens ?? 0)),
            TotalThinkingTokens: turns.Sum(t => (long)(t.ThinkingTokens ?? 0)),
            TotalCostUsd: turns.Sum(t => t.CostUsd ?? 0),
            AvgSlotCompleteness: withSlot.Count > 0 ? withSlot.Average(t => (double)t.SlotCompleteness!.Value) : 0,
            ErrorRate: (double)errorCount / turns.Count,
            FinishReasonBreakdown: finishBreakdown,
            ErrorCodeBreakdown: errorBreakdown));
    }
}
