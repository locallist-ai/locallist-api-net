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
    public DbSet<RouteSegmentCache> RouteSegmentCaches { get; set; } = null!;
    public DbSet<ChatSession> ChatSessions { get; set; } = null!;
    public DbSet<UserProfile> UserProfiles { get; set; } = null!;
    public DbSet<ChatTurn> ChatTurns { get; set; } = null!;
    public DbSet<PlanMetric> PlanMetrics { get; set; } = null!;
    public DbSet<Subcategory> Subcategories { get; set; } = null!;
    public DbSet<BillingEvent> BillingEvents { get; set; } = null!;
    public DbSet<UsageCounter> UsageCounters { get; set; } = null!;

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
        modelBuilder.Entity<Place>().Property(p => p.BestTimes).HasColumnType("text[]");
        modelBuilder.Entity<Place>().Property(p => p.SuitableFor).HasColumnType("text[]");
        modelBuilder.Entity<Place>().Property(p => p.Photos).HasColumnType("text[]");
        modelBuilder.Entity<Place>().Property(p => p.Flags).HasColumnType("text[]");

        // pgvector — 768-dim embeddings (Gemini gemini-embedding-001, L2-norm)
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

        modelBuilder.Entity<RouteSegmentCache>()
            .HasOne(r => r.FromPlace)
            .WithMany()
            .HasForeignKey(r => r.FromPlaceId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RouteSegmentCache>()
            .HasOne(r => r.ToPlace)
            .WithMany()
            .HasForeignKey(r => r.ToPlaceId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RouteSegmentCache>()
            .HasIndex(r => new { r.FromPlaceId, r.ToPlaceId, r.Mode })
            .IsUnique();

        modelBuilder.Entity<ChatSession>()
            .HasOne(cs => cs.User)
            .WithMany()
            .HasForeignKey(cs => cs.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ChatSession>()
            .HasIndex(cs => new { cs.UserId, cs.Status });

        modelBuilder.Entity<ChatSession>()
            .HasIndex(cs => cs.AnonymousIpHash);

        modelBuilder.Entity<ChatSession>()
            .HasIndex(cs => cs.LastTurnAt);

        modelBuilder.Entity<ChatSession>()
            .Property(cs => cs.LastOfferedChips)
            .HasColumnType("text[]");

        modelBuilder.Entity<UserProfile>()
            .HasOne(up => up.User)
            .WithOne()
            .HasForeignKey<UserProfile>(up => up.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserProfile>()
            .Property(up => up.CompanionTags)
            .HasColumnType("text[]");

        modelBuilder.Entity<UserProfile>()
            .Property(up => up.DietaryRestrictions)
            .HasColumnType("text[]");

        // ChatTurn — FK to chat_sessions (nullable; builder legacy has no session)
        modelBuilder.Entity<ChatTurn>()
            .HasOne(ct => ct.Session)
            .WithMany()
            .HasForeignKey(ct => ct.SessionId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired(false);

        modelBuilder.Entity<ChatTurn>()
            .HasOne(ct => ct.User)
            .WithMany()
            .HasForeignKey(ct => ct.UserId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        modelBuilder.Entity<ChatTurn>()
            .HasIndex(ct => ct.CreatedAt);

        modelBuilder.Entity<ChatTurn>()
            .HasIndex(ct => new { ct.UserId, ct.CreatedAt });

        modelBuilder.Entity<ChatTurn>()
            .HasIndex(ct => new { ct.PromptVersion, ct.CreatedAt });

        // Partial unique index: (session_id, turn_index) only when session_id is not null
        modelBuilder.Entity<ChatTurn>()
            .HasIndex(ct => new { ct.SessionId, ct.TurnIndex })
            .IsUnique()
            .HasFilter("session_id IS NOT NULL");

        // PlanMetric — one-to-one with plans
        modelBuilder.Entity<PlanMetric>()
            .HasOne(pm => pm.Plan)
            .WithMany()
            .HasForeignKey(pm => pm.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PlanMetric>()
            .HasIndex(pm => pm.PlanId)
            .IsUnique();

        modelBuilder.Entity<PlanMetric>()
            .HasIndex(pm => pm.CreatedAt);

        modelBuilder.Entity<PlanMetric>()
            .HasIndex(pm => new { pm.GenerationSource, pm.CreatedAt });

        modelBuilder.Entity<PlanMetric>()
            .HasIndex(pm => new { pm.PromptVersion, pm.CreatedAt });

        modelBuilder.Entity<Subcategory>()
            .HasQueryFilter(s => s.DeletedAt == null);

        modelBuilder.Entity<Subcategory>()
            .HasIndex(s => new { s.CategoryKey, s.Key })
            .IsUnique()
            .HasFilter("deleted_at IS NULL");

        // Billing — RevenueCat webhook idempotency ledger. Unique rc_event_id is the
        // dedup arbiter (a concurrent duplicate delivery loses on INSERT). The
        // (user_id, event_timestamp_ms) index backs the reorder guard lookup.
        modelBuilder.Entity<BillingEvent>()
            .HasIndex(be => be.RcEventId)
            .IsUnique();

        modelBuilder.Entity<BillingEvent>()
            .HasIndex(be => new { be.UserId, be.EventTimestampMs });

        // Usage counters (F4 — gates Plus). PK compuesta = target del ON CONFLICT del
        // upsert atómico de UsageCounterService. FK con cascade: DELETE /account
        // arrastra los contadores (GDPR); el reset-por-reregistro que eso permite
        // queda acotado por el techo horario por IP de los endpoints medidos.
        modelBuilder.Entity<UsageCounter>()
            .HasKey(uc => new { uc.UserId, uc.Feature, uc.PeriodStart });

        modelBuilder.Entity<UsageCounter>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(uc => uc.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
