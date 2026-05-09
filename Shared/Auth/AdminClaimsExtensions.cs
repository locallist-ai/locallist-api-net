using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace LocalList.API.NET.Shared.Auth;

public static class AdminClaimsExtensions
{
    private const string FirebaseIssuerPrefix = "https://securetoken.google.com/";
    private const string AdminDomain = "@locallist.ai";

    /// <summary>
    /// Returns true only for Firebase RS256 tokens with a @locallist.ai email.
    /// Rejects HS256 app tokens regardless of email — prevents confused-deputy attacks.
    /// </summary>
    public static bool IsAdminCaller(this ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        var issuer = user.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Iss)?.Value;
        var email = user.GetEmail();
        return !string.IsNullOrEmpty(issuer)
            && issuer.StartsWith(FirebaseIssuerPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(email)
            && email.EndsWith(AdminDomain, StringComparison.OrdinalIgnoreCase);
    }
}
