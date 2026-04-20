using System.ComponentModel.DataAnnotations;

namespace LocalList.API.NET.Features.Auth;

public record SyncUserDto(Guid Id, string Email, string? Name, string? Image, string Tier, string Role);

public record SyncResponse(SyncUserDto User);

// ─── App auth DTOs (JWT propio HS256) ────────────────────

public record AppAuthUser(Guid Id, string Email, string? Name, string? Image, string Tier);

public record AppAuthResponse(string AccessToken, string RefreshToken, AppAuthUser User);

public record SigninRequest(
    [Required, RegularExpression("^(apple|google)$")] string Provider,
    [Required, StringLength(8192, MinimumLength = 1)] string IdToken,
    [StringLength(255)] string? Name);

public record RegisterRequest(
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password,
    [StringLength(255, MinimumLength = 1)] string? Name);

public record LoginRequest(
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(128, MinimumLength = 1)] string Password);

public record RefreshRequest(
    [Required, StringLength(256, MinimumLength = 16)] string RefreshToken);
