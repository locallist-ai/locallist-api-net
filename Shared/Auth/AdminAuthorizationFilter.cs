using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LocalList.API.NET.Shared.Auth;

public class AdminAuthorizationFilter : IAsyncAuthorizationFilter
{
    private const string AdminDomain = "@locallist.ai";
    private const string ApiKeyHeader = "X-Admin-Key";
    private readonly ILogger<AdminAuthorizationFilter> _logger;
    private readonly IConfiguration _configuration;

    public AdminAuthorizationFilter(ILogger<AdminAuthorizationFilter> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Option 1: Admin API Key (for scripts and dev operations)
        if (context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeader, out var apiKey))
        {
            var expectedKey = _configuration["ADMIN_API_KEY"]
                ?? Environment.GetEnvironmentVariable("ADMIN_API_KEY");

            if (!string.IsNullOrEmpty(expectedKey) && string.Equals(apiKey, expectedKey, StringComparison.Ordinal))
            {
                _logger.LogInformation("Admin access granted via API Key");
                return Task.CompletedTask;
            }

            _logger.LogWarning("Admin access denied: invalid API Key");
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid admin API key." });
            return Task.CompletedTask;
        }

        // Option 2: Firebase JWT with @locallist.ai email (for app/ERP)
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
