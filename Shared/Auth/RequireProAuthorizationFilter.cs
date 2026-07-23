using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Shared.Auth;

/// <summary>
/// Authorization filter behind <see cref="RequireProAttribute"/>. Resolves the caller to a
/// LocalList user and requires <c>tier == "pro"</c>, read fresh from the DB (never the JWT claim).
/// Works for both token schemes: App HS256 puts the user Guid in <c>sub</c>; Firebase RS256 puts
/// an opaque uid resolved via <see cref="FirebaseUserExtensions.GetUserIdAsync"/>.
/// </summary>
public class RequireProAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<RequireProAuthorizationFilter> _logger;

    public RequireProAuthorizationFilter(
        LocalListDbContext db, ILogger<RequireProAuthorizationFilter> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var principal = context.HttpContext.User;
        var ct = context.HttpContext.RequestAborted;

        if (!principal.Identity?.IsAuthenticated ?? true)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Authentication required." });
            return;
        }

        var userId = await principal.GetUserIdAsync(_db, ct);
        if (userId is null)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid token claims." });
            return;
        }

        // Re-query the tier from the DB — the JWT claim is not trusted (stale + forgeable).
        var tier = await _db.Users
            .Where(u => u.Id == userId.Value)
            .Select(u => u.Tier)
            .FirstOrDefaultAsync(ct);

        if (!string.Equals(tier, "pro", StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Pro-gated endpoint denied for user {UserId} (tier={Tier})", userId, tier ?? "none");
            // 403: authenticated but lacks entitlement. Structured body so the app can prompt upgrade.
            context.Result = new ObjectResult(new { error = "pro_required" })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }
    }
}
