using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LocalList.API.NET.Shared.Auth;

public class AdminAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminAuthorizationFilter> _logger;

    public AdminAuthorizationFilter(IConfiguration configuration, ILogger<AdminAuthorizationFilter> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;

        if (!user.Identity?.IsAuthenticated ?? true)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Authentication required." });
            return Task.CompletedTask;
        }

        var email = user.FindFirstValue(JwtRegisteredClaimNames.Email)
                    ?? user.FindFirstValue(ClaimTypes.Email);

        if (string.IsNullOrEmpty(email))
        {
            _logger.LogWarning("Admin access denied: no email claim in token");
            context.Result = new ForbidResult();
            return Task.CompletedTask;
        }

        var adminEmails = _configuration.GetSection("Admin:Emails").Get<string[]>() ?? [];

        if (!adminEmails.Contains(email, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Admin access denied for {Email}", email);
            context.Result = new ForbidResult();
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }
}
