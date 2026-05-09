using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LocalList.API.NET.Shared.Auth;

public class AdminAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly ILogger<AdminAuthorizationFilter> _logger;

    public AdminAuthorizationFilter(ILogger<AdminAuthorizationFilter> logger)
    {
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

        if (!user.IsAdminCaller())
        {
            var issuer = user.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Iss)?.Value;
            _logger.LogWarning("Admin access denied for user {FirebaseUid} (issuer={Issuer})",
                user.GetFirebaseUid() ?? "unknown", issuer ?? "none");
            context.Result = new ForbidResult();
        }

        return Task.CompletedTask;
    }
}
