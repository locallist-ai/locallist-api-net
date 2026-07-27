using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Usage;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Fixture con <c>Dev:TierOverrideEnabled=true</c> — modela un entorno de test/dev donde el
/// override de tier está ABIERTO. Container propio (config distinta del default, que lo tiene off).
/// </summary>
public sealed class DevTierOverrideEnabledFixture : ApiFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Dev:TierOverrideEnabled", "true");
        base.ConfigureWebHost(builder);
    }
}

/// <summary>
/// Fixture PEOR-CASO de misconfig de prod: flag <c>Dev:TierOverrideEnabled=true</c> Y entorno
/// <c>Production</c> (lo que Railway pone). El gate 0 (entorno) debe ganar → 404 aunque el flag esté
/// on y el email sea interno. Prueba que un flag mal puesto en prod sigue fail-closed.
/// <c>UseEnvironment("Production")</c> va DESPUÉS de <c>base</c> (que fija Development) para que gane.
/// </summary>
public sealed class DevTierOverrideProductionFixture : ApiFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Dev:TierOverrideEnabled", "true");
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
    }
}

/// <summary>
/// Gate 0 (entorno) — el que hace que un misconfig del flag en prod no explote. Con flag ON +
/// Production + email interno + tier válido → 404 y <c>User.Tier</c> intacto. Mutación: quitar el
/// gate de entorno → devuelve 200 y flipa el tier (el test cae).
/// </summary>
public class AccountDevTierProductionTests(DevTierOverrideProductionFixture fixture)
    : IClassFixture<DevTierOverrideProductionFixture>
{
    [Fact]
    public async Task Production_FlagOn_InternalEmail_ValidTier_Returns404_NoFlip()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"dev-prod-{uid:N}@locallist.ai", tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        // El gate 0 corta antes de tocar la DB: tier intacto pese a flag on + email interno.
        var db = fixture.GetDbContext();
        var user = await db.Users.FirstAsync(u => u.Id == uid);
        Assert.Equal("free", user.Tier);
    }

    [Fact]
    public async Task Production_FlagOn_InternalEmail_ResetQuota_Returns404_NoDelete()
    {
        // El gate 0 (entorno) también protege reset-quota: prod + flag on + email interno → 404,
        // y las cuotas NO se borran. Mutación: quitar el gate de entorno → 200 y borra.
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"dev-prod-reset-{uid:N}@locallist.ai", tier: "free");
        var db = fixture.GetDbContext();
        db.UsageCounters.Add(new UsageCounter
        {
            UserId = uid,
            Feature = PlanGenerationGateService.FeatureMonthly,
            PeriodStart = new DateOnly(2026, 7, 1),
            Count = 3,
        });
        await db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync("/account/dev/reset-quota", new { });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        var db2 = fixture.GetDbContext();
        Assert.Equal(1, await db2.UsageCounters.CountAsync(c => c.UserId == uid));
    }
}

/// <summary>
/// F-dev — <c>POST /account/dev/tier</c> sobre DB real (ApiFixture = Testcontainers PostgreSQL).
/// Verifica el triple gate FAIL-CLOSED del override de tier con el flag ENCENDIDO:
/// email @locallist.ai → aplica el tier en la DB (no-vacuo: se relee la fila), email ajeno → 404
/// opaco (mutación del gate de email), tier inválido → 400, y que el efecto va SIEMPRE al usuario
/// del token (jamás a un id del body). El 404 con flag apagado vive en
/// <see cref="AccountDevTierDisabledTests"/> (fixture con el default off).
/// </summary>
public class AccountDevTierTests(DevTierOverrideEnabledFixture fixture)
    : IClassFixture<DevTierOverrideEnabledFixture>
{
    private static async Task<string> TierInDb(ApiFixture f, Guid userId)
    {
        var db = f.GetDbContext();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        return user.Tier;
    }

    [Fact]
    public async Task FlagOn_InternalEmail_FlipsTierInDb_ProThenFree()
    {
        var uid = Guid.NewGuid();
        // Seed con tier free y email interno @locallist.ai.
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"dev-tier-{uid:N}@locallist.ai", tier: "free");

        // free → pro
        var toPro = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.OK, toPro.StatusCode);
        var proBody = await toPro.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pro", proBody.GetProperty("tier").GetString());
        Assert.Equal("pro", await TierInDb(fixture, uid)); // mutación real en la DB

        // pro → free
        var toFree = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "free" });
        Assert.Equal(HttpStatusCode.OK, toFree.StatusCode);
        var freeBody = await toFree.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("free", freeBody.GetProperty("tier").GetString());
        Assert.Equal("free", await TierInDb(fixture, uid));
    }

    [Fact]
    public async Task FlagOn_NonInternalEmail_Returns404_EvenThoughFlagOn()
    {
        // Gate de email: el flag está ON pero el email NO es @locallist.ai → 404 opaco.
        // Mutación: si se quita el check de dominio, este 404 pasaría a 200 y el test cae.
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"dev-tier-{uid:N}@gmail.com", tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("free", await TierInDb(fixture, uid)); // sin efecto en la DB
    }

    [Fact]
    public async Task FlagOn_LookalikeDomain_Returns404()
    {
        // Defensa del EndsWith: un dominio que solo CONTIENE @locallist.ai pero no termina en él
        // (p. ej. @locallist.ai.evil.com) no debe pasar el gate → 404.
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"dev-tier-{uid:N}@locallist.ai.evil.com", tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("free", await TierInDb(fixture, uid));
    }

    [Theory]
    [InlineData("plus")]
    [InlineData("PRO")]
    [InlineData("")]
    [InlineData("admin")]
    public async Task FlagOn_InternalEmail_InvalidTier_Returns400(string badTier)
    {
        // Gate 3: tier estricto {pro,free}. Otro valor → 400 (caller ya validado por gates 1+2).
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"dev-tier-bad-{uid:N}@locallist.ai", tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = badTier });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("free", await TierInDb(fixture, uid)); // sin cambio de tier
    }

    [Fact]
    public async Task FlagOn_MissingTier_Returns400()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"dev-tier-null-{uid:N}@locallist.ai", tier: "free");

        // Body sin campo tier → Tier=null → 400.
        var res = await client.PostAsJsonAsync("/account/dev/tier", new { });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("free", await TierInDb(fixture, uid));
    }

    [Fact]
    public async Task FlagOn_Anonymous_Returns401()
    {
        // Sin token: la auth (AppScheme) rechaza antes de entrar al action → 401, no 404.
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task FlagOn_IgnoresBodyId_MutatesOnlyTokenUser()
    {
        // El efecto va SIEMPRE al usuario del token, NUNCA a un id del body. Sembramos una víctima
        // y mandamos su id en el body: la víctima NO debe cambiar; el caller SÍ.
        var callerId = Guid.NewGuid();
        var victimId = Guid.NewGuid();

        var db = fixture.GetDbContext();
        db.Users.Add(new User
        {
            Id = victimId,
            Email = $"victim-{victimId:N}@locallist.ai",
            FirebaseUid = "app-" + victimId,
            Tier = "free",
        });
        await db.SaveChangesAsync();

        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            callerId, $"caller-{callerId:N}@locallist.ai", tier: "free");

        // Campos id/userId sobran en el DTO (solo se bindea Tier) — se ignoran a propósito.
        var res = await client.PostAsJsonAsync("/account/dev/tier",
            new { tier = "pro", id = victimId, userId = victimId, sub = victimId });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal("pro", await TierInDb(fixture, callerId));  // el del token cambió
        Assert.Equal("free", await TierInDb(fixture, victimId)); // la víctima intacta
    }

    // ── /account/dev/reset-quota — mismos gates que /tier ────────────────────────────────

    private static async Task SeedCounter(ApiFixture f, Guid userId, string feature, int count)
    {
        var db = f.GetDbContext();
        db.UsageCounters.Add(new UsageCounter
        {
            UserId = userId,
            Feature = feature,
            PeriodStart = new DateOnly(2026, 7, 1),
            Count = count,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> CounterRows(ApiFixture f, Guid userId)
    {
        var db = f.GetDbContext();
        return await db.UsageCounters.CountAsync(c => c.UserId == userId);
    }

    [Fact]
    public async Task ResetQuota_FlagOn_InternalEmail_ClearsCallerCounters()
    {
        var uid = Guid.NewGuid();
        // El usuario debe existir antes de sembrar usage_counters (FK user_id).
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"dev-reset-{uid:N}@locallist.ai", tier: "free");

        // Consume cuota: contador mensual de planes (free = 3/mes) + una ventana de import.
        await SeedCounter(fixture, uid, PlanGenerationGateService.FeatureMonthly, 3);
        await SeedCounter(fixture, uid, "import_daily", 5);
        Assert.Equal(2, await CounterRows(fixture, uid));

        var res = await client.PostAsJsonAsync("/account/dev/reset-quota", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("reset").GetInt32());

        // Mutación: sin el DELETE del endpoint, estas filas seguirían y el count no sería 0.
        Assert.Equal(0, await CounterRows(fixture, uid));

        using var scope = fixture.Services.CreateScope();
        var used = await scope.ServiceProvider.GetRequiredService<IUsageCounterService>()
            .GetUsedAsync(uid, PlanGenerationGateService.FeatureMonthly, new DateOnly(2026, 7, 1), default);
        Assert.Equal(0, used);
    }

    [Fact]
    public async Task ResetQuota_FlagOn_OnlyDeletesCallerRows_NotOthers()
    {
        // El borrado es por el id del TOKEN: la cuota de otro usuario NO se toca.
        var callerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        // Ambos usuarios deben existir antes de sembrar sus counters (FK user_id).
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            callerId, $"dev-reset-scope-{callerId:N}@locallist.ai", tier: "free");
        var db0 = fixture.GetDbContext();
        db0.Users.Add(new User
        {
            Id = otherId,
            Email = $"other-{otherId:N}@locallist.ai",
            FirebaseUid = "app-" + otherId,
            Tier = "free",
        });
        await db0.SaveChangesAsync();

        await SeedCounter(fixture, otherId, PlanGenerationGateService.FeatureMonthly, 3);
        await SeedCounter(fixture, callerId, PlanGenerationGateService.FeatureMonthly, 3);
        var res = await client.PostAsJsonAsync("/account/dev/reset-quota", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal(0, await CounterRows(fixture, callerId)); // caller limpio
        Assert.Equal(1, await CounterRows(fixture, otherId));  // ajeno intacto
    }

    [Fact]
    public async Task ResetQuota_FlagOn_NonInternalEmail_Returns404_NoDelete()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"dev-reset-{uid:N}@gmail.com", tier: "free");
        await SeedCounter(fixture, uid, PlanGenerationGateService.FeatureMonthly, 3);

        var res = await client.PostAsJsonAsync("/account/dev/reset-quota", new { });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(1, await CounterRows(fixture, uid)); // gate de email cortó antes del DELETE
    }

    [Fact]
    public async Task ResetQuota_FlagOn_Anonymous_Returns401()
    {
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/account/dev/reset-quota", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}

/// <summary>
/// Prueba el gate PRIMARIO (config flag) con el DEFAULT de producción: <c>Dev:TierOverrideEnabled</c>
/// ausente → false. Con el flag apagado el endpoint es 404 OPACO para TODOS, incluido un usuario
/// @locallist.ai legítimo. Mutación: si se quita el check del flag, este 404 pasa a 200 (o 400/OK)
/// y el test cae — es la prueba de que en PROD el override no existe para nadie.
/// </summary>
public class AccountDevTierDisabledTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task FlagOffByDefault_InternalEmail_Returns404()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"dev-off-{uid:N}@locallist.ai", tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        // El tier NO cambió: el endpoint no llegó a tocar la DB.
        var db = fixture.GetDbContext();
        var user = await db.Users.FirstAsync(u => u.Id == uid);
        Assert.Equal("free", user.Tier);
    }

    [Fact]
    public async Task FlagOffByDefault_Anonymous_Returns401()
    {
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ResetQuota_FlagOffByDefault_InternalEmail_Returns404_NoDelete()
    {
        // Mismo gate 1 (flag) para reset-quota: apagado → 404 y las cuotas NO se borran.
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"dev-reset-off-{uid:N}@locallist.ai", tier: "free");
        var db = fixture.GetDbContext();
        db.UsageCounters.Add(new UsageCounter
        {
            UserId = uid,
            Feature = PlanGenerationGateService.FeatureMonthly,
            PeriodStart = new DateOnly(2026, 7, 1),
            Count = 3,
        });
        await db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync("/account/dev/reset-quota", new { });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        var db2 = fixture.GetDbContext();
        Assert.Equal(1, await db2.UsageCounters.CountAsync(c => c.UserId == uid));
    }
}
