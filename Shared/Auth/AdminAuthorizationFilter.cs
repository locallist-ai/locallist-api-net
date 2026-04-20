using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LocalList.API.NET.Shared.Auth;

public class AdminAuthorizationFilter : IAsyncAuthorizationFilter
{
    private const string AdminDomain = "@locallist.ai";
    private readonly ILogger<AdminAuthorizationFilter> _logger;

    public AdminAuthorizationFilter(ILogger<AdminAuthorizationFilter> logger)
    {
        _logger = logger;
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Single auth path: Firebase JWT with @locallist.ai email.
        // Removed legacy X-Admin-Key shared-secret path (2026-04-20) — see
        // memory: decision_admin_endpoints_via_firebase_jwt.md.
        var user = context.HttpContext.User;

        if (!user.Identity?.IsAuthenticated ?? true)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Authentication required." });
            return Task.CompletedTask;
        }

        var email = user.GetEmail();

        if (string.IsNullOrEmpty(email) || !email.EndsWith(AdminDomain, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Admin access denied for user {FirebaseUid}",
                user.GetFirebaseUid() ?? "unknown");
            context.Result = new ForbidResult();
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }
}
