using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Profile;

[ApiController]
[Route("me/profile")]
[Authorize]
public class ProfileController : ControllerBase
{
    private readonly LocalListDbContext _db;

    public ProfileController(LocalListDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null) return Unauthorized();

        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile == null) return NoContent();

        return Ok(ToResponse(profile));
    }

    [HttpPut]
    public async Task<IActionResult> Upsert([FromBody] UpsertProfileRequest request, CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null) return Unauthorized();

        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile == null)
        {
            profile = new UserProfile { UserId = userId.Value };
            _db.UserProfiles.Add(profile);
        }

        if (request.DefaultGroupType != null)
            profile.DefaultGroupType = request.DefaultGroupType;
        if (request.CompanionTags != null)
            profile.CompanionTags = request.CompanionTags.Take(10).ToList();
        if (request.DietaryRestrictions != null)
            profile.DietaryRestrictions = request.DietaryRestrictions.Take(10).ToList();
        if (request.PacePreference != null)
            profile.PacePreference = request.PacePreference;
        if (request.DefaultBudgetTier != null)
            profile.DefaultBudgetTier = request.DefaultBudgetTier;
        if (request.FavoriteCity != null)
            profile.FavoriteCity = request.FavoriteCity;

        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(ToResponse(profile));
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null) return Unauthorized();

        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile == null) return NoContent();

        _db.UserProfiles.Remove(profile);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static ProfileResponse ToResponse(UserProfile p) => new()
    {
        DefaultGroupType = p.DefaultGroupType,
        CompanionTags = p.CompanionTags,
        DietaryRestrictions = p.DietaryRestrictions,
        PacePreference = p.PacePreference,
        DefaultBudgetTier = p.DefaultBudgetTier,
        FavoriteCity = p.FavoriteCity,
        UpdatedAt = p.UpdatedAt,
    };
}
