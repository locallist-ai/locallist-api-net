using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LocalList.API.NET.Shared.Auth;

public class AdminAuthorizationFilter : IAsyncAuthorizationFilter
{
    private const string AdminDomain = "@locallist.ai";
    // Firebase tokens are issued by https://securetoken.google.com/{projectId}.
    // App HS256 tokens use issuer "locallist-api". Rejecting non-Firebase issuers
    // prevents an attacker from registering a @locallist.ai email via /auth/register
    // and using the resulting HS256 token to access admin endpoints.
    private const string FirebaseIssuerPrefix = "https://securetoken.google.com/";
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

        var issuer = user.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Iss)?.Value;
        var email = user.GetEmail();

        if (string.IsNullOrEmpty(issuer) ||
            !issuer.StartsWith(FirebaseIssuerPrefix, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(email) ||
            !email.EndsWith(AdminDomain, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Admin access denied for user {FirebaseUid} (issuer={Issuer})",
                user.GetFirebaseUid() ?? "unknown", issuer ?? "none");
            context.Result = new ForbidResult();
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }
}
