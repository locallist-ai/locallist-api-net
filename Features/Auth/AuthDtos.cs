using System.ComponentModel.DataAnnotations;

namespace LocalList.API.NET.Features.Auth;

public class LoginRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(100)]
    public string Password { get; set; } = string.Empty;
}

public class RegisterRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(100)]
    public string Password { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Name { get; set; }
}

public class OAuthRequest
{
    [Required]
    public string Provider { get; set; } = string.Empty;

    [Required]
    public string IdToken { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Name { get; set; }
}

public class RefreshRequest
{
    [Required]
    public string RefreshToken { get; set; } = string.Empty;
}
