using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Data.Models;

[Table("users")]
public class User
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("email")]
    [StringLength(255)]
    [Required]
    public string Email { get; set; } = string.Empty;

    [Column("name")]
    [StringLength(255)]
    public string? Name { get; set; }

    [Column("image")]
    public string? Image { get; set; }

    [Column("tier")]
    [StringLength(20)]
    public string Tier { get; set; } = "free";

    [Column("password_hash")]
    [StringLength(255)]
    public string? PasswordHash { get; set; }

    [Column("apple_user_id")]
    [StringLength(255)]
    public string? AppleUserId { get; set; }

    [Column("google_user_id")]
    [StringLength(255)]
    public string? GoogleUserId { get; set; }

    [Column("rc_customer_id")]
    [StringLength(255)]
    public string? RcCustomerId { get; set; }

    [Column("city")]
    [StringLength(100)]
    public string? City { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    
    // Navigation properties
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ICollection<Plan> CreatedPlans { get; set; } = new List<Plan>();
    public ICollection<Place> SubmittedPlaces { get; set; } = new List<Place>();
    public ICollection<Place> ReviewedPlaces { get; set; } = new List<Place>();
    public ICollection<FollowSession> FollowSessions { get; set; } = new List<FollowSession>();
}
