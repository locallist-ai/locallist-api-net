using System.ComponentModel.DataAnnotations;

namespace LocalList.API.NET.Features.Auth;

public record SyncUserDto(Guid Id, string Email, string? Name, string? Image, string Tier, string Role);

public record SyncResponse(SyncUserDto User);

// ─── App auth DTOs (JWT propio HS256) ────────────────────

public record AppAuthUser(Guid Id, string Email, string? Name, string? Image, string Tier);

public record AppAuthResponse(string AccessToken, string RefreshToken, AppAuthUser User);

public record SigninRequest(
    [property: Required, RegularExpression("^(apple|google)$")] string Provider,
    [property: Required, StringLength(8192, MinimumLength = 1)] string IdToken,
    [property: StringLength(255)] string? Name);

public record RegisterRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(128, MinimumLength = 8)] string Password,
    [property: StringLength(255, MinimumLength = 1)] string? Name);

public record LoginRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(128, MinimumLength = 1)] string Password);

public record RefreshRequest(
    [property: Required, StringLength(256, MinimumLength = 16)] string RefreshToken);
