using System.Text.Json;
using LocalList.API.NET.Features.Social.Entities;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Esquema social (S0): back-compat visibility<->is_public, backfill de la migracion, y las
/// invariantes de las tablas nuevas (unicidad de handle citext y share_token, CHECK de self-follow,
/// cascade GDPR). DB real (Testcontainers): la migracion se aplica de verdad.
/// </summary>
public class SocialFoundationSchemaTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<Guid> SeedUser()
    {
        var id = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = id, Email = $"soc-{id:N}@test.com", FirebaseUid = $"fb-{id:N}" });
        await db.SaveChangesAsync();
        return id;
    }

    // ── Back-compat entity: escribir IsPublic mantiene Visibility coherente y viceversa ──
    [Theory]
    [InlineData(true, "public")]
    [InlineData(false, "private")]
    public async Task WritingIsPublic_KeepsVisibilityInSync_RoundTripsThroughDb(bool isPublic, string expectedVisibility)
    {
        var owner = await SeedUser();
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Sync", City = "Miami", Type = "personal", IsPublic = isPublic, CreatedById = owner };
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var reloaded = await fixture.GetDbContext().Plans.AsNoTracking().FirstAsync(p => p.Id == plan.Id);
        Assert.Equal(expectedVisibility, reloaded.Visibility);
        Assert.Equal(isPublic, reloaded.IsPublic);
    }

    [Theory]
    [InlineData("public", true)]
    [InlineData("private", false)]
    [InlineData("unlisted", false)]
    public async Task WritingVisibility_KeepsIsPublicInSync_RoundTripsThroughDb(string visibility, bool expectedIsPublic)
    {
        var owner = await SeedUser();
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Sync2", City = "Miami", Type = "personal", Visibility = visibility, CreatedById = owner };
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var reloaded = await fixture.GetDbContext().Plans.AsNoTracking().FirstAsync(p => p.Id == plan.Id);
        Assert.Equal(visibility, reloaded.Visibility);
        Assert.Equal(expectedIsPublic, reloaded.IsPublic);
    }

    // ── Backfill de la migracion: un plan legacy con is_public=true (y visibility a la deriva en
    // 'private', simulando el estado pre-backfill) debe quedar 'public' tras el UPDATE de backfill. ──
    [Fact]
    public async Task Backfill_LegacyIsPublicTrue_BecomesVisibilityPublic()
    {
        var owner = await SeedUser();
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Legacy", City = "Miami", Type = "curated", IsPublic = true, CreatedById = owner };
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        // Simula el estado pre-backfill: is_public=true pero visibility todavia 'private'.
        await db.Database.ExecuteSqlRawAsync("UPDATE plans SET visibility = 'private' WHERE id = {0}", plan.Id);
        // Re-ejecuta la MISMA sentencia de backfill que corre la migracion AddSocialFoundation.
        await db.Database.ExecuteSqlRawAsync("UPDATE plans SET visibility = 'public' WHERE is_public = true");

        var reloaded = await fixture.GetDbContext().Plans.AsNoTracking().FirstAsync(p => p.Id == plan.Id);
        Assert.Equal("public", reloaded.Visibility);
        Assert.True(reloaded.IsPublic);
    }

    // ── Back-compat DTO: la respuesta publica sigue exponiendo isPublic derivado de visibility. ──
    [Fact]
    public async Task PublicPlan_DtoStillExposesIsPublicTrue()
    {
        var owner = await SeedUser();
        var plan = new Plan { Id = Guid.NewGuid(), Name = "DTO Public", City = "Miami", Type = "curated", Visibility = "public", CreatedById = owner };
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/plans/{plan.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isPublic").GetBoolean());
    }

    [Fact]
    public async Task PrivatePlan_OwnerDto_ExposesIsPublicFalse()
    {
        var ownerId = Guid.NewGuid();
        var fbUid = $"fb-dtopriv-{ownerId:N}";
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = ownerId, Email = $"dtopriv-{ownerId:N}@test.com", FirebaseUid = fbUid });
        var plan = new Plan { Id = Guid.NewGuid(), Name = "DTO Private", City = "Miami", Type = "personal", Visibility = "private", CreatedById = ownerId };
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(ownerId, fbUid);
        var response = await client.GetAsync($"/plans/{plan.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isPublic").GetBoolean());
    }

    // ── user_public_profiles.handle: citext UNIQUE (case-insensitive) ──
    [Fact]
    public async Task Handle_IsUnique_CaseInsensitive()
    {
        var u1 = await SeedUser();
        var u2 = await SeedUser();

        var db1 = fixture.GetDbContext();
        db1.UserPublicProfiles.Add(new UserPublicProfile { UserId = u1, Handle = "Alice", DisplayName = "Alice" });
        await db1.SaveChangesAsync();

        var db2 = fixture.GetDbContext();
        db2.UserPublicProfiles.Add(new UserPublicProfile { UserId = u2, Handle = "alice", DisplayName = "Alice2" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    // ── plans.share_token: UNIQUE ──
    [Fact]
    public async Task ShareToken_IsUnique()
    {
        var owner = await SeedUser();
        var db1 = fixture.GetDbContext();
        db1.Plans.Add(new Plan { Id = Guid.NewGuid(), Name = "Tok1", City = "Miami", Type = "personal", CreatedById = owner, ShareToken = "dupTok123" });
        await db1.SaveChangesAsync();

        var db2 = fixture.GetDbContext();
        db2.Plans.Add(new Plan { Id = Guid.NewGuid(), Name = "Tok2", City = "Miami", Type = "personal", CreatedById = owner, ShareToken = "dupTok123" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    // ── plans.share_token: NULL permite multiples (unique no cuenta NULLs) ──
    [Fact]
    public async Task ShareToken_MultipleNullsAllowed()
    {
        var owner = await SeedUser();
        var db = fixture.GetDbContext();
        db.Plans.Add(new Plan { Id = Guid.NewGuid(), Name = "N1", City = "Miami", Type = "personal", CreatedById = owner });
        db.Plans.Add(new Plan { Id = Guid.NewGuid(), Name = "N2", City = "Miami", Type = "personal", CreatedById = owner });
        await db.SaveChangesAsync(); // no debe lanzar
    }

    // ── user_follows: CHECK (follower_id <> followee_id) ──
    [Fact]
    public async Task UserFollow_SelfFollow_RejectedByCheckConstraint()
    {
        var u = await SeedUser();
        var db = fixture.GetDbContext();
        db.UserFollows.Add(new UserFollow { FollowerId = u, FolloweeId = u });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task UserFollow_DistinctUsers_Allowed()
    {
        var a = await SeedUser();
        var b = await SeedUser();
        var db = fixture.GetDbContext();
        db.UserFollows.Add(new UserFollow { FollowerId = a, FolloweeId = b });
        await db.SaveChangesAsync(); // no debe lanzar
    }

    // ── Cascade GDPR: borrar la cuenta cascadea el grafo (aqui: user_follows) ──
    [Fact]
    public async Task DeletingUser_CascadesUserFollows()
    {
        var a = await SeedUser();
        var b = await SeedUser();
        var db = fixture.GetDbContext();
        db.UserFollows.Add(new UserFollow { FollowerId = a, FolloweeId = b });
        await db.SaveChangesAsync();

        var delDb = fixture.GetDbContext();
        var userA = await delDb.Users.FirstAsync(u => u.Id == a);
        delDb.Users.Remove(userA);
        await delDb.SaveChangesAsync();

        var verifyDb = fixture.GetDbContext();
        Assert.False(await verifyDb.UserFollows.AnyAsync(f => f.FollowerId == a || f.FolloweeId == a));
    }
}
