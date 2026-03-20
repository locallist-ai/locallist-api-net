using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Shared.Auth;

public static class FirebaseUserExtensions
{
    public static string? GetFirebaseUid(this ClaimsPrincipal user)
    {
        return user.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? user.FindFirstValue("user_id")
               ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub);
    }

    public static string? GetEmail(this ClaimsPrincipal user)
    {
        return user.FindFirstValue(ClaimTypes.Email)
               ?? user.FindFirstValue(JwtRegisteredClaimNames.Email);
    }

    public static async Task<Guid?> GetUserIdAsync(this ClaimsPrincipal user, LocalListDbContext db, CancellationToken ct = default)
    {
        var firebaseUid = user.GetFirebaseUid();
        if (string.IsNullOrEmpty(firebaseUid)) return null;

        return await db.Users
            .Where(u => u.FirebaseUid == firebaseUid)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
    }
}
