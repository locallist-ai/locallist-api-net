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
    public DbSet<VideoImportMetric> VideoImportMetrics { get; set; } = null!;
    public DbSet<BillingEvent> BillingEvents { get; set; } = null!;
    public DbSet<UsageCounter> UsageCounters { get; set; } = null!;
    public DbSet<CityRequest> CityRequests { get; set; } = null!;
    public DbSet<Favorite> Favorites { get; set; } = null!;

    // ── Social foundation (S0) ──
    public DbSet<UserPublicProfile> UserPublicProfiles { get; set; } = null!;
    public DbSet<UserFollow> UserFollows { get; set; } = null!;
    public DbSet<PlanCollaborator> PlanCollaborators { get; set; } = null!;
    public DbSet<PlanInvite> PlanInvites { get; set; } = null!;
    public DbSet<ActivityEvent> ActivityEvents { get; set; } = null!;
    public DbSet<PlanLike> PlanLikes { get; set; } = null!;
    public DbSet<ContentReport> ContentReports { get; set; } = null!;
    public DbSet<UserBlock> UserBlocks { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");
        // citext: handles case-insensitive con unicidad (user_public_profiles.handle).
        modelBuilder.HasPostgresExtension("citext");

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
        // Social S0: la visibilidad pasa a ser la fuente de verdad; migramos (city, is_public) ->
        // (city, visibility). is_public sigue como columna espejo (sin indice propio nuevo).
        modelBuilder.Entity<Plan>().HasIndex(p => new { p.City, p.Visibility });
        // Column default 'private' (seguro); el CLR default de la entidad es 'public' (compat con
        // el comportamiento historico is_public=true) y EF siempre envia el valor explicito.
        modelBuilder.Entity<Plan>().Property(p => p.Visibility).HasDefaultValue("private");
        modelBuilder.Entity<Plan>().HasIndex(p => p.ShareToken).IsUnique();
        // Feed: planes publicos ordenados por publicacion (published_at DESC).
        modelBuilder.Entity<Plan>().HasIndex(p => new { p.Visibility, p.PublishedAt }).IsDescending(false, true);
        // cloned_from: origen del clone del share-link (S1). Self-FK SET NULL (no navegacion).
        modelBuilder.Entity<Plan>()
            .HasOne<Plan>()
            .WithMany()
            .HasForeignKey(p => p.ClonedFrom)
            .OnDelete(DeleteBehavior.SetNull);

        ConfigureSocial(modelBuilder);

        modelBuilder.Entity<PlanStop>().HasIndex(ps => new { ps.PlanId, ps.DayNumber });

        modelBuilder.Entity<FollowSession>().HasIndex(fs => new { fs.UserId, fs.Status });

        modelBuilder.Entity<WaitlistEntry>().HasIndex(w => w.Email).IsUnique();

        // City requests (feedback "¿No ves tu ciudad?"). user_id FK SET NULL — el
        // peticionario puede ser invitado y la petición sobrevive al borrado de cuenta.
        modelBuilder.Entity<CityRequest>()
            .HasOne(cr => cr.User)
            .WithMany()
            .HasForeignKey(cr => cr.UserId)
            .OnDelete(DeleteBehavior.SetNull);
        // Índice no-único para agregar "top ciudades pedidas".
        modelBuilder.Entity<CityRequest>().HasIndex(cr => cr.NormalizedCity);
        // Índice por created_at para la ventana de dedup 24h y ordenación temporal.
        modelBuilder.Entity<CityRequest>().HasIndex(cr => cr.CreatedAt);

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

        // VideoImportMetric — diagnóstico standalone del import de vídeo (sin FK).
        modelBuilder.Entity<VideoImportMetric>()
            .HasIndex(v => v.CreatedAt);

        modelBuilder.Entity<VideoImportMetric>()
            .HasIndex(v => new { v.Platform, v.CreatedAt });

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

        // Favoritos (F-BE). PK compuesta (user_id, place_id) = índice único que hace idempotente
        // el favoritar (el segundo del mismo par choca con el 23505, tragado por el controller).
        // Ambos FK CASCADE: borrar cuenta (GDPR) o place arrastra los favoritos. Índice
        // (user_id, created_at DESC) para el listado paginado ordenado por recencia.
        modelBuilder.Entity<Favorite>()
            .HasKey(f => new { f.UserId, f.PlaceId });
        modelBuilder.Entity<Favorite>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Favorite>()
            .HasOne<Place>()
            .WithMany()
            .HasForeignKey(f => f.PlaceId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Favorite>()
            .HasIndex(f => new { f.UserId, f.CreatedAt })
            .IsDescending(false, true);
    }

    /// <summary>
    /// Esquema social (S0). Todos los FK a users con ON DELETE CASCADE (borrado de cuenta cascadea
    /// el grafo social — GDPR), salvo los explicitamente SET NULL (content_reports.reporter_id,
    /// plan_collaborators.invited_by), que sobreviven al borrado de la cuenta. Las entidades sin
    /// navegacion a User se configuran con HasOne&lt;User&gt;().WithMany() (dos FK a users por tabla
    /// son validos en Postgres).
    /// </summary>
    private static void ConfigureSocial(ModelBuilder modelBuilder)
    {
        // ── UserPublicProfile ──
        modelBuilder.Entity<UserPublicProfile>()
            .HasOne(up => up.User)
            .WithOne()
            .HasForeignKey<UserPublicProfile>(up => up.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserPublicProfile>()
            .HasIndex(up => up.Handle)
            .IsUnique();

        // ── UserFollow ──
        modelBuilder.Entity<UserFollow>()
            .HasKey(f => new { f.FollowerId, f.FolloweeId });
        modelBuilder.Entity<UserFollow>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(f => f.FollowerId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserFollow>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(f => f.FolloweeId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserFollow>()
            .ToTable(t => t.HasCheckConstraint("ck_user_follows_no_self", "follower_id <> followee_id"));
        modelBuilder.Entity<UserFollow>()
            .HasIndex(f => new { f.FolloweeId, f.CreatedAt })
            .IsDescending(false, true);
        modelBuilder.Entity<UserFollow>()
            .HasIndex(f => new { f.FollowerId, f.CreatedAt })
            .IsDescending(false, true);

        // ── PlanCollaborator ──
        modelBuilder.Entity<PlanCollaborator>()
            .HasKey(c => new { c.PlanId, c.UserId });
        modelBuilder.Entity<PlanCollaborator>()
            .HasOne<Plan>()
            .WithMany()
            .HasForeignKey(c => c.PlanId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PlanCollaborator>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PlanCollaborator>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.InvitedBy)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<PlanCollaborator>()
            .HasIndex(c => c.UserId);

        // ── PlanInvite ──
        modelBuilder.Entity<PlanInvite>()
            .HasOne<Plan>()
            .WithMany()
            .HasForeignKey(i => i.PlanId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PlanInvite>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(i => i.CreatedBy)
            .OnDelete(DeleteBehavior.Cascade);

        // ── ActivityEvent ──
        modelBuilder.Entity<ActivityEvent>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.ActorId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ActivityEvent>()
            .HasIndex(a => new { a.ActorId, a.CreatedAt })
            .IsDescending(false, true);
        modelBuilder.Entity<ActivityEvent>()
            .HasIndex(a => a.CreatedAt)
            .IsDescending();
        modelBuilder.Entity<ActivityEvent>()
            .HasIndex(a => new { a.ActorId, a.Verb, a.ObjectId })
            .IsUnique();

        // ── PlanLike ──
        modelBuilder.Entity<PlanLike>()
            .HasKey(l => new { l.PlanId, l.UserId });
        modelBuilder.Entity<PlanLike>()
            .HasOne<Plan>()
            .WithMany()
            .HasForeignKey(l => l.PlanId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PlanLike>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(l => l.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PlanLike>()
            .HasIndex(l => new { l.UserId, l.CreatedAt })
            .IsDescending(false, true);

        // ── ContentReport ──
        modelBuilder.Entity<ContentReport>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(r => r.ReporterId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<ContentReport>()
            .HasIndex(r => new { r.Status, r.CreatedAt });

        // ── UserBlock ──
        modelBuilder.Entity<UserBlock>()
            .HasKey(b => new { b.BlockerId, b.BlockedId });
        modelBuilder.Entity<UserBlock>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(b => b.BlockerId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserBlock>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(b => b.BlockedId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
