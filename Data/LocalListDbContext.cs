using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Data.Models;

namespace LocalList.API.NET.Data;

public class LocalListDbContext : DbContext
{
    public LocalListDbContext(DbContextOptions<LocalListDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; } = null!;
    public DbSet<RefreshToken> RefreshTokens { get; set; } = null!;
    public DbSet<Place> Places { get; set; } = null!;
    public DbSet<Plan> Plans { get; set; } = null!;
    public DbSet<PlanStop> PlanStops { get; set; } = null!;
    public DbSet<FollowSession> FollowSessions { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Constraints and Cascade deletes matching the PostgreSQL drizzle schema

        modelBuilder.Entity<RefreshToken>()
            .HasOne(rt => rt.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(rt => rt.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PlanStop>()
            .HasOne(ps => ps.Plan)
            .WithMany(p => p.Stops)
            .HasForeignKey(ps => ps.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PlanStop>()
            .HasOne(ps => ps.Place)
            .WithMany(p => p.PlanStops)
            .HasForeignKey(ps => ps.PlaceId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<FollowSession>()
            .HasOne(fs => fs.User)
            .WithMany(u => u.FollowSessions)
            .HasForeignKey(fs => fs.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Map explicit indices mentioned in schema.ts
        modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.AppleUserId).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.GoogleUserId).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.RcCustomerId).IsUnique();

        modelBuilder.Entity<RefreshToken>().HasIndex(rt => rt.TokenPrefix);
        modelBuilder.Entity<RefreshToken>().HasIndex(rt => rt.UserId);

        modelBuilder.Entity<Place>().HasIndex(p => new { p.Status, p.City });
        modelBuilder.Entity<Place>().HasIndex(p => p.Category);

        modelBuilder.Entity<Plan>().HasIndex(p => p.CreatedById);
        modelBuilder.Entity<Plan>().HasIndex(p => new { p.City, p.IsPublic });

        modelBuilder.Entity<PlanStop>().HasIndex(ps => new { ps.PlanId, ps.DayNumber });

        modelBuilder.Entity<FollowSession>().HasIndex(fs => new { fs.UserId, fs.Status });
    }
}
