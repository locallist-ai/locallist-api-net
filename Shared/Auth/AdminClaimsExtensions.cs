using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace LocalList.API.NET.Shared.Auth;

public static class AdminClaimsExtensions
{
    private const string FirebaseIssuerPrefix = "https://securetoken.google.com/";
    private const string AdminDomain = "@locallist.ai";

    /// <summary>
    /// Returns true only for Firebase RS256 tokens with a verified @locallist.ai email.
    /// Rejects HS256 app tokens regardless of email — prevents confused-deputy attacks.
    /// Requires email_verified == true so that a self-signed-up email/password account for
    /// x@locallist.ai (whose mailbox the attacker does not control, so it can never be
    /// verified) cannot escalate to admin. Google SSO always sets email_verified=true.
    /// </summary>
    public static bool IsAdminCaller(this ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        var issuer = user.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Iss)?.Value;
        var email = user.GetEmail();
        return !string.IsNullOrEmpty(issuer)
            && issuer.StartsWith(FirebaseIssuerPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(email)
            && email.EndsWith(AdminDomain, StringComparison.OrdinalIgnoreCase)
            && user.HasVerifiedEmail();
    }

    /// <summary>
    /// True when the Firebase token carries email_verified == true. The claim value is a JSON
    /// boolean upstream; bool.TryParse tolerates "true"/"True" casing across serializers.
    /// </summary>
    private static bool HasVerifiedEmail(this ClaimsPrincipal user)
    {
        var emailVerified = user.Claims.FirstOrDefault(c => c.Type == "email_verified")?.Value;
        return bool.TryParse(emailVerified, out var verified) && verified;
    }
}
