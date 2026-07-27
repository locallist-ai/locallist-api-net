using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalList.API.NET.Features.Plans;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Social S1 — compartir un plan por enlace. Endpoints POST/DELETE /plans/{id}/share (owner) y
/// GET /plans/shared/{token} (anónimo). DB real (Testcontainers): nada se mockea.
/// </summary>
public class PlanShareTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<Guid> SeedUser(string tag)
    {
        var id = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = id, Email = $"{tag}-{id:N}@test.com", FirebaseUid = $"fb-{tag}-{id:N}" });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Plan> SeedPlan(
        Guid ownerId, string visibility, string? shareToken = null, bool withStop = false,
        string? tripContextJson = null)
    {
        var db = fixture.GetDbContext();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Sharable Plan",
            City = "Miami",
            Type = "custom",
            DurationDays = 1,
            Visibility = visibility,
            ShareToken = shareToken,
            CreatedById = ownerId,
            TripContext = tripContextJson is null ? null : JsonDocument.Parse(tripContextJson),
        };
        db.Plans.Add(plan);
        if (withStop)
        {
            var place = new Place
            {
                Id = Guid.NewGuid(), Name = "Shared Stop Place", Category = "Food",
                WhyThisPlace = "Nice", Status = "published"
            };
            db.Places.Add(place);
            db.PlanStops.Add(new PlanStop
            {
                Id = Guid.NewGuid(), PlanId = plan.Id, PlaceId = place.Id,
                DayNumber = 1, OrderIndex = 0, TimeBlock = "morning"
            });
        }
        await db.SaveChangesAsync();
        return plan;
    }

    private Task<HttpClient> AppClient(Guid userId, string tag) =>
        fixture.CreateAppAuthenticatedClientWithUser(userId, $"{tag}-{userId:N}@test.com");

    // ── (a) Owner comparte un plan privado → token + private→unlisted ──
    [Fact]
    public async Task Share_OwnerOnPrivatePlan_GeneratesToken_AndPromotesToUnlisted()
    {
        var owner = await SeedUser("shareowner");
        var plan = await SeedPlan(owner, "private");
        var client = await AppClient(owner, "shareowner-c");

        var res = await client.PostAsync($"/plans/{plan.Id}/share", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("shareToken").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        Assert.Equal("unlisted", body.GetProperty("visibility").GetString());

        // Fuente de verdad en DB: unlisted + is_public espejo = false + token persistido.
        var db = fixture.GetDbContext();
        var persisted = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == plan.Id);
        Assert.Equal("unlisted", persisted.Visibility);
        Assert.False(persisted.IsPublic);
        Assert.Equal(token, persisted.ShareToken);
    }

    // ── (a) Re-share es idempotente: mismo token, no re-baja visibilidad ──
    [Fact]
    public async Task Share_Twice_IsIdempotent_SameToken()
    {
        var owner = await SeedUser("reshare");
        var plan = await SeedPlan(owner, "private");
        var client = await AppClient(owner, "reshare-c");

        var first = await (await client.PostAsync($"/plans/{plan.Id}/share", null)).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await client.PostAsync($"/plans/{plan.Id}/share", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(first.GetProperty("shareToken").GetString(), second.GetProperty("shareToken").GetString());
        Assert.Equal("unlisted", second.GetProperty("visibility").GetString());
    }

    // ── (a) Un plan ya público al compartir: genera token pero SIGUE public ──
    [Fact]
    public async Task Share_PublicPlan_KeepsPublic_AddsToken()
    {
        var owner = await SeedUser("sharepub");
        var plan = await SeedPlan(owner, "public");
        var client = await AppClient(owner, "sharepub-c");

        var body = await (await client.PostAsync($"/plans/{plan.Id}/share", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("public", body.GetProperty("visibility").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("shareToken").GetString()));
    }

    // ── (a) editor / viewer / stranger NO comparten → 404 opaco ──
    [Theory]
    [InlineData("editor")]
    [InlineData("viewer")]
    [InlineData("stranger")]
    public async Task Share_NonOwner_Returns404(string role)
    {
        var owner = await SeedUser("nonown-owner");
        var actor = await SeedUser($"nonown-{role}");
        var plan = await SeedPlan(owner, "private");

        if (role is "editor" or "viewer")
        {
            var db = fixture.GetDbContext();
            db.PlanCollaborators.Add(new PlanCollaborator { PlanId = plan.Id, UserId = actor, Role = role, InvitedBy = owner });
            await db.SaveChangesAsync();
        }

        var client = await AppClient(actor, $"nonown-{role}-c");
        var res = await client.PostAsync($"/plans/{plan.Id}/share", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        // No se generó token para el plan.
        var db2 = fixture.GetDbContext();
        var persisted = await db2.Plans.AsNoTracking().FirstAsync(p => p.Id == plan.Id);
        Assert.Null(persisted.ShareToken);
        Assert.Equal("private", persisted.Visibility);
    }

    [Fact]
    public async Task Share_NonExistentPlan_Returns404()
    {
        var owner = await SeedUser("shareghost");
        var client = await AppClient(owner, "shareghost-c");
        var res = await client.PostAsync($"/plans/{Guid.NewGuid()}/share", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Share_Anonymous_Returns401()
    {
        var owner = await SeedUser("shareanon");
        var plan = await SeedPlan(owner, "private");
        var client = fixture.CreateClient();
        var res = await client.PostAsync($"/plans/{plan.Id}/share", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── (b) GET por token anónimo → 200 solo-lectura SIN campos de dueño ──
    [Fact]
    public async Task GetShared_UnlistedByToken_Anonymous_Returns200_WithoutOwnerFields()
    {
        var owner = await SeedUser("getshared");
        var plan = await SeedPlan(owner, "unlisted", shareToken: ShareTokenGenerator.Generate(), withStop: true);
        var anon = fixture.CreateClient();

        var res = await anon.GetAsync($"/plans/shared/{plan.ShareToken}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Sharable Plan", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("days").GetArrayLength() >= 1);
        // Identificador del dueño excluido (WhenWritingNull → ausente, o explícitamente null).
        if (body.TryGetProperty("createdById", out var cid))
            Assert.Equal(JsonValueKind.Null, cid.ValueKind);
    }

    [Fact]
    public async Task GetShared_PublicByToken_Anonymous_Returns200()
    {
        var owner = await SeedUser("getsharedpub");
        var plan = await SeedPlan(owner, "public", shareToken: ShareTokenGenerator.Generate());
        var anon = fixture.CreateClient();
        var res = await anon.GetAsync($"/plans/shared/{plan.ShareToken}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ── (b) token inválido / inexistente → 404 opaco ──
    [Fact]
    public async Task GetShared_UnknownToken_Returns404()
    {
        var anon = fixture.CreateClient();
        var res = await anon.GetAsync($"/plans/shared/{ShareTokenGenerator.Generate()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── (b) plan que volvió a private con token viejo → 404 INDISTINGUIBLE del inexistente ──
    [Fact]
    public async Task GetShared_PlanRevertedToPrivate_WithStaleToken_Returns404_Indistinguishable()
    {
        var owner = await SeedUser("stale");
        // Estado imposible por la API pero defendible: token presente + visibility private (p.ej.
        // una futura revocación que dejara el token). El endpoint NUNCA sirve un plan private.
        var staleToken = ShareTokenGenerator.Generate();
        var plan = await SeedPlan(owner, "private", shareToken: staleToken);
        var anon = fixture.CreateClient();

        var revoked = await anon.GetAsync($"/plans/shared/{staleToken}");
        var unknown = await anon.GetAsync($"/plans/shared/{ShareTokenGenerator.Generate()}");

        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        // Cuerpos idénticos: no se puede distinguir "token existió pero private" de "nunca existió".
        Assert.Equal(await revoked.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    // ── (c) revoke → token null + unlisted→private ──
    [Fact]
    public async Task Revoke_UnlistedPlan_ClearsToken_AndRevertsToPrivate()
    {
        var owner = await SeedUser("revoke");
        var plan = await SeedPlan(owner, "unlisted", shareToken: ShareTokenGenerator.Generate());
        var client = await AppClient(owner, "revoke-c");

        var res = await client.DeleteAsync($"/plans/{plan.Id}/share");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var db = fixture.GetDbContext();
        var persisted = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == plan.Id);
        Assert.Null(persisted.ShareToken);
        Assert.Equal("private", persisted.Visibility);
        Assert.False(persisted.IsPublic);
    }

    // ── (c) revoke de un plan público → sigue public, solo quita el token ──
    [Fact]
    public async Task Revoke_PublicPlan_StaysPublic_TokenCleared()
    {
        var owner = await SeedUser("revokepub");
        var plan = await SeedPlan(owner, "public", shareToken: ShareTokenGenerator.Generate());
        var client = await AppClient(owner, "revokepub-c");

        var res = await client.DeleteAsync($"/plans/{plan.Id}/share");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var db = fixture.GetDbContext();
        var persisted = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == plan.Id);
        Assert.Null(persisted.ShareToken);
        Assert.Equal("public", persisted.Visibility);
        Assert.True(persisted.IsPublic);
    }

    // ── (c) revoke idempotente: revocar un plan sin token → 204 igual ──
    [Fact]
    public async Task Revoke_NeverShared_IsIdempotent_204()
    {
        var owner = await SeedUser("revokenoop");
        var plan = await SeedPlan(owner, "private");
        var client = await AppClient(owner, "revokenoop-c");

        var res = await client.DeleteAsync($"/plans/{plan.Id}/share");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task Revoke_NonOwner_Returns404()
    {
        var owner = await SeedUser("revokeowner");
        var stranger = await SeedUser("revokestranger");
        var plan = await SeedPlan(owner, "unlisted", shareToken: ShareTokenGenerator.Generate());
        var client = await AppClient(stranger, "revokestranger-c");

        var res = await client.DeleteAsync($"/plans/{plan.Id}/share");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        // El token del owner NO se tocó.
        var db = fixture.GetDbContext();
        var persisted = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == plan.Id);
        Assert.NotNull(persisted.ShareToken);
    }

    // ── Round-trip completo: share → GET por token 200 → revoke → GET por token 404 ──
    [Fact]
    public async Task ShareThenRevoke_TokenStopsResolving()
    {
        var owner = await SeedUser("roundtrip");
        var plan = await SeedPlan(owner, "private", withStop: true);
        var client = await AppClient(owner, "roundtrip-c");
        var anon = fixture.CreateClient();

        var shareBody = await (await client.PostAsync($"/plans/{plan.Id}/share", null)).Content.ReadFromJsonAsync<JsonElement>();
        var token = shareBody.GetProperty("shareToken").GetString();

        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/plans/shared/{token}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/plans/{plan.Id}/share")).StatusCode);

        // Tras revocar: el mismo token ya no resuelve (plan volvió a private + token null).
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/plans/shared/{token}")).StatusCode);
    }

    // ── (b) privacidad: el DTO compartido NO contiene tripContext (dieta, presupuesto,
    // groupType... del dueño). Aserción sobre el JSON crudo: el campo debe estar AUSENTE
    // (WhenWritingNull), no solo null. El GET dueño-por-GUID sí lo sirve (control).
    [Fact]
    public async Task GetShared_DoesNotExpose_TripContext()
    {
        var owner = await SeedUser("privtc");
        const string sensitiveTripContext =
            """{"city":"Miami","groupType":"couple","budgetAmount":900,"dietaryRestrictions":["halal"]}""";
        var plan = await SeedPlan(owner, "unlisted", shareToken: ShareTokenGenerator.Generate(),
            withStop: true, tripContextJson: sensitiveTripContext);
        var anon = fixture.CreateClient();

        var res = await anon.GetAsync($"/plans/shared/{plan.ShareToken}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var raw = await res.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.False(body.TryGetProperty("tripContext", out _), "tripContext no debe salir en el DTO anónimo");
        // Ni rastro de los valores sensibles en el JSON crudo (defensa contra renombrados).
        Assert.DoesNotContain("halal", raw);
        Assert.DoesNotContain("budgetAmount", raw);

        // Control: el owner por GUID SÍ recibe su tripContext (solo se recorta el path anónimo).
        var ownerClient = await AppClient(owner, "privtc-c");
        var ownBody = await (await ownerClient.GetAsync($"/plans/{plan.Id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(ownBody.TryGetProperty("tripContext", out var tc) && tc.ValueKind == JsonValueKind.Object);
    }

    // ── (a/c) re-share tras revoke: token DISTINTO — el enlace revocado no revive ──
    [Fact]
    public async Task Share_AfterRevoke_ReturnsDifferentToken_OldTokenStaysDead()
    {
        var owner = await SeedUser("rerevoke");
        var plan = await SeedPlan(owner, "private", withStop: true);
        var client = await AppClient(owner, "rerevoke-c");
        var anon = fixture.CreateClient();

        var first = await (await client.PostAsync($"/plans/{plan.Id}/share", null)).Content.ReadFromJsonAsync<JsonElement>();
        var oldToken = first.GetProperty("shareToken").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/plans/{plan.Id}/share")).StatusCode);

        var second = await (await client.PostAsync($"/plans/{plan.Id}/share", null)).Content.ReadFromJsonAsync<JsonElement>();
        var newToken = second.GetProperty("shareToken").GetString();

        Assert.False(string.IsNullOrEmpty(newToken));
        Assert.NotEqual(oldToken, newToken); // revocar invalida DE VERDAD: el re-share no resucita el enlace viejo
        // El token viejo sigue muerto; el nuevo resuelve.
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/plans/shared/{oldToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/plans/shared/{newToken}")).StatusCode);
    }

    // ── (d) MINOR a: un plan 'unlisted' JAMÁS aparece en el listado público GET /plans ──
    [Fact]
    public async Task GetPlans_Authenticated_DoesNotList_UnlistedPlans()
    {
        var owner = await SeedUser("listunlisted");
        // 'unlisted' con is_public espejo=false; y uno 'public' de control para asegurar que el
        // endpoint sí devuelve algo.
        var unlisted = await SeedPlan(owner, "unlisted", shareToken: ShareTokenGenerator.Generate());
        var db = fixture.GetDbContext();
        var publicPlan = new Plan
        {
            Id = Guid.NewGuid(), Name = "Public Control", City = "Miami", Type = "curated",
            Visibility = "public", IsShowcase = false, CreatedById = owner
        };
        db.Plans.Add(publicPlan);
        await db.SaveChangesAsync();

        var client = await AppClient(owner, "listunlisted-c");
        var res = await client.GetAsync("/plans");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("plans").EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(publicPlan.Id, ids);
        Assert.DoesNotContain(unlisted.Id, ids);
    }

    // ── (h) el token no es adivinable secuencialmente: formato/longitud/unicidad ──
    [Fact]
    public async Task ShareToken_HasUrlSafeFormat_AndIsUnique_AcrossPlans()
    {
        var owner = await SeedUser("entropy");
        var client = await AppClient(owner, "entropy-c");
        var tokens = new HashSet<string>();

        for (var i = 0; i < 8; i++)
        {
            var plan = await SeedPlan(owner, "private");
            var body = await (await client.PostAsync($"/plans/{plan.Id}/share", null)).Content.ReadFromJsonAsync<JsonElement>();
            var token = body.GetProperty("shareToken").GetString()!;

            Assert.Equal(ShareTokenGenerator.TokenLength, token.Length);
            Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), token); // URL-safe, sin padding
            Assert.True(tokens.Add(token), "share tokens deben ser únicos (no secuenciales/repetidos)");
        }
    }
}
