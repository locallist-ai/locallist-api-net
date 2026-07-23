using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Usage;

namespace LocalList.API.NET.Features.Account;

[ApiController]
[Route("account")]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<AccountController> _logger;
    private readonly IUsageCounterService _counters;
    private readonly TimeProvider _time;

    public AccountController(
        LocalListDbContext db,
        ILogger<AccountController> logger,
        IUsageCounterService counters,
        TimeProvider time)
    {
        _db = db;
        _logger = logger;
        _counters = counters;
        _time = time;
    }

    [HttpGet]
    public async Task<IActionResult> GetAccount(CancellationToken ct)
    {
        var sub = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(sub))
            return Unauthorized(new { error = "Invalid token claims" });

        var query = Guid.TryParse(sub, out var hsUserId)
            ? _db.Users.Where(u => u.Id == hsUserId)
            : _db.Users.Where(u => u.FirebaseUid == sub);

        var user = await query
            .Select(u => new
            {
                id = u.Id,
                email = u.Email,
                name = u.Name,
                image = u.Image,
                tier = u.Tier,
                role = u.Role,
                city = u.City,
                createdAt = u.CreatedAt
            })
            .FirstOrDefaultAsync(ct);

        if (user == null)
            return NotFound(new { error = "User not found" });

        // Cuota de generación IA mensual, expuesta proactivamente (m4/F7) para que la app
        // pinte "X de 3 planes este mes" sin tener que provocar el 403. Contrato ESTABLE con
        // el task app-side: aiPlansMonth = { used, limit, resetsAt }. Para Plus (`pro`) el
        // límite mensual no aplica (usan el cap diario antiabuso) → limit omitido = ilimitado;
        // used sigue siendo el contador mensual (0 para Plus). resetsAt = inicio del mes
        // siguiente (UTC), el momento en que el contador free se resetea.
        var isPro = string.Equals(user.tier, PlanGenerationGateService.TierPro, StringComparison.Ordinal);
        var now = _time.GetUtcNow();
        var monthStart = new DateOnly(now.Year, now.Month, 1);
        var used = await _counters.GetUsedAsync(user.id, PlanGenerationGateService.FeatureMonthly, monthStart, ct);
        var resetsAt = new DateTimeOffset(
            monthStart.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var aiPlansMonth = new
        {
            used,
            limit = isPro ? (int?)null : PlanGenerationGateService.FreeMonthlyPlanLimit,
            resetsAt,
        };

        return Ok(new { user, aiPlansMonth });
    }

    // Apple Guideline 5.1.1(v) - Account deletion
    [HttpDelete]
    public async Task<IActionResult> DeleteAccount(CancellationToken ct)
    {
        var sub = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(sub))
            return Unauthorized(new { error = "Invalid token claims" });

        var user = Guid.TryParse(sub, out var hsUserId)
            ? await _db.Users.FirstOrDefaultAsync(u => u.Id == hsUserId, ct)
            : await _db.Users.FirstOrDefaultAsync(u => u.FirebaseUid == sub, ct);

        if (user == null)
            return NotFound(new { error = "User not found" });

        var userId = user.Id;

        // Nullify references in plans and places via bulk update (no N+1, no race conditions)
        await _db.Plans.Where(p => p.CreatedById == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedById, (Guid?)null), ct);
        await _db.Places.Where(p => p.SubmittedById == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SubmittedById, (Guid?)null), ct);
        await _db.Places.Where(p => p.ReviewedById == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ReviewedById, (Guid?)null), ct);

        // Delete user (FollowSessions will cascade automatically via DB constraints)
        _db.Users.Remove(user);

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Account deleted: {UserId}", userId);
        return Ok(new { message = "Account deleted" });
    }
}
