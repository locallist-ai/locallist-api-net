using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Shared.Data;

public class LocalListDbContext : DbContext
{
    public LocalListDbContext(DbContextOptions<LocalListDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Place> Places { get; set; } = null!;
    public DbSet<Plan> Plans { get; set; } = null!;
    public DbSet<PlanStop> PlanStops { get; set; } = null!;
    public DbSet<FollowSession> FollowSessions { get; set; } = null!;
    public DbSet<WaitlistEntry> WaitlistEntries { get; set; } = null!;
    public DbSet<RefreshToken> RefreshTokens { get; set; } = null!;
    public DbSet<City> Cities { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");

        // Constraints and Cascade deletes matching the PostgreSQL drizzle schema

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

        modelBuilder.Entity<FollowSession>()
            .HasOne(fs => fs.Plan)
            .WithMany(p => p.FollowSessions)
            .HasForeignKey(fs => fs.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Plan>()
            .HasOne(p => p.CreatedBy)
            .WithMany(u => u.CreatedPlans)
            .HasForeignKey(p => p.CreatedById)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Place>()
            .HasOne(p => p.SubmittedBy)
            .WithMany(u => u.SubmittedPlaces)
            .HasForeignKey(p => p.SubmittedById)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Place>()
            .HasOne(p => p.ReviewedBy)
            .WithMany(u => u.ReviewedPlaces)
            .HasForeignKey(p => p.ReviewedById)
            .OnDelete(DeleteBehavior.SetNull);

        // Map explicit indices mentioned in schema.ts
        modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.AppleUserId).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.GoogleUserId).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.RcCustomerId).IsUnique();
        modelBuilder.Entity<User>().HasIndex(u => u.FirebaseUid).IsUnique();

        modelBuilder.Entity<Place>().HasIndex(p => new { p.Status, p.City });
        modelBuilder.Entity<Place>().HasIndex(p => p.Category);
        modelBuilder.Entity<Place>().HasIndex(p => p.GooglePlaceId).IsUnique();

        // Cities — unique normalized_name evita duplicados (Miami/miami/MIAMI).
        modelBuilder.Entity<City>().HasIndex(c => c.NormalizedName).IsUnique();

        // Explicit array column types for Npgsql
        modelBuilder.Entity<Place>().Property(p => p.BestFor).HasColumnType("text[]");
        modelBuilder.Entity<Place>().Property(p => p.SuitableFor).HasColumnType("text[]");
        modelBuilder.Entity<Place>().Property(p => p.Photos).HasColumnType("text[]");
        modelBuilder.Entity<Place>().Property(p => p.Flags).HasColumnType("text[]");

        // pgvector — 768-dim embeddings (Gemini text-embedding-004)
        modelBuilder.Entity<Place>().Property(p => p.Embedding).HasColumnType("vector(768)");

        modelBuilder.Entity<Plan>().HasIndex(p => p.CreatedById);
        modelBuilder.Entity<Plan>().HasIndex(p => new { p.City, p.IsPublic });

        modelBuilder.Entity<PlanStop>().HasIndex(ps => new { ps.PlanId, ps.DayNumber });

        modelBuilder.Entity<FollowSession>().HasIndex(fs => new { fs.UserId, fs.Status });

        modelBuilder.Entity<WaitlistEntry>().HasIndex(w => w.Email).IsUnique();

        modelBuilder.Entity<RefreshToken>()
            .HasOne(rt => rt.User)
            .WithMany()
            .HasForeignKey(rt => rt.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RefreshToken>().HasIndex(rt => rt.TokenPrefix);
        modelBuilder.Entity<RefreshToken>().HasIndex(rt => rt.UserId);
    }
}
