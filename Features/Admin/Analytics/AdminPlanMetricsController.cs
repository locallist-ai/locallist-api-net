using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Admin.Analytics;

[ApiController]
[Route("admin/analytics/plan-metrics")]
[AdminAuthorize]
[EnableRateLimiting("AdminLimit")]
public class AdminPlanMetricsController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<AdminPlanMetricsController> _logger;

    public AdminPlanMetricsController(LocalListDbContext db, ILogger<AdminPlanMetricsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Paginated list of plan generation metrics with optional filters.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] string? city = null,
        [FromQuery] string? source = null,
        [FromQuery] bool? wasOpened = null,
        [FromQuery] bool? wasFollowed = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);

        IQueryable<PlanMetric> query = _db.PlanMetrics.AsNoTracking().Include(m => m.Plan);

        if (city != null)           query = query.Where(m => m.Plan != null && m.Plan.City == city);
        if (source != null)         query = query.Where(m => m.GenerationSource == source);
        if (wasOpened.HasValue)     query = query.Where(m => m.WasOpened == wasOpened.Value);
        if (wasFollowed.HasValue)   query = query.Where(m => m.WasFollowed == wasFollowed.Value);
        if (from.HasValue)          query = query.Where(m => m.CreatedAt >= from.Value);
        if (to.HasValue)            query = query.Where(m => m.CreatedAt <= to.Value);

        var total = await query.CountAsync(ct);
        var metrics = await query
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id) // tiebreaker: orden total y estable con CreatedAt empatado (paginación sin dups/omisiones)
            .Skip(offset)
            .Take(limit)
            .Select(m => new AdminPlanMetricDto(
                m.Id, m.CreatedAt, m.PlanId,
                m.Plan != null ? m.Plan.Name : null,
                m.Plan != null ? m.Plan.City : null,
                m.GenerationSource, m.SignalsFilled,
                m.NumDays, m.NumStops, m.NumCategories,
                m.GroupType, m.Budget, m.LatencyMs, m.CostUsd,
                m.WasOpened, m.OpenedAt, m.WasFollowed, m.FollowedAt,
                m.EditedCount, m.Regenerated))
            .ToListAsync(ct);

        return Ok(new AdminPlanMetricsListResponse(metrics, total, limit, offset));
    }

    /// <summary>Aggregated plan quality stats, optionally scoped to a date range or city.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? city = null,
        CancellationToken ct = default)
    {
        IQueryable<PlanMetric> query = _db.PlanMetrics.AsNoTracking().Include(m => m.Plan);

        if (city != null)  query = query.Where(m => m.Plan != null && m.Plan.City == city);
        if (from.HasValue) query = query.Where(m => m.CreatedAt >= from.Value);
        if (to.HasValue)   query = query.Where(m => m.CreatedAt <= to.Value);

        var rows = await query
            .Select(m => new
            {
                m.WasOpened, m.WasFollowed, m.LatencyMs, m.CostUsd,
                City = m.Plan != null ? m.Plan.City : null
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return Ok(new AdminPlanMetricsStatsDto(0, 0, 0, 0, 0, new List<AdminPlanMetricsByCityDto>()));
        }

        var byCity = rows
            .Where(r => r.City != null)
            .GroupBy(r => r.City!)
            .Select(g => new AdminPlanMetricsByCityDto(
                City: g.Key,
                Count: g.Count(),
                OpenRate: g.Count(r => r.WasOpened) / (double)g.Count(),
                FollowRate: g.Count(r => r.WasFollowed) / (double)g.Count()))
            .OrderByDescending(b => b.Count)
            .ToList();

        return Ok(new AdminPlanMetricsStatsDto(
            TotalPlans: rows.Count,
            OpenRate: rows.Count(r => r.WasOpened) / (double)rows.Count,
            FollowRate: rows.Count(r => r.WasFollowed) / (double)rows.Count,
            AvgLatencyMs: rows.Average(r => (double)r.LatencyMs),
            TotalCostUsd: rows.Sum(r => r.CostUsd ?? 0),
            ByCity: byCity));
    }
}
