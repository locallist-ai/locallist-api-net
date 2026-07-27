using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Usage;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Fixture con <c>Dev:TierOverrideEnabled=true</c> Y un <c>Dev:AllowedEmails</c> concreto — modela un
/// entorno (incl. PROD) donde el override está ABIERTO para un conjunto EXACTO de cuentas internas.
/// Container propio (config distinta del default, que tiene el flag off y el allowlist vacío).
///
/// El match es EXACTO byte-a-byte (Ordinal, case-sensitive, SIN trim). El gate sigue siendo la
/// defensa-en-profundidad del self-upgrade por variante; desde la higiene de email <c>users.email</c>
/// es <c>citext</c> (case-insensitive único) + normalización en escritura, así que una variante de
/// caja ya NO puede ni materializarse como fila propia — el gate Ordinal la rechazaría igualmente si
/// existiese (p.ej. dato legado sin normalizar). El allowlist incluye bases con las que se ejercita
/// el BLOCKER de bypass por variante:
///   • <see cref="CaseBaseAllowlisted"/> — allowlisteado y sembrado EN SU MISMA caja → pasa (200).
///   • <see cref="CaseVariantBaseAllowlisted"/> — allowlisteado pero NUNCA sembrado en minúsculas;
///     el atacante siembra su VARIANTE en mayúsculas (<see cref="CaseVariantAttacker"/>): como la
///     base no se materializa, la variante entra como fila propia (no choca con el índice único
///     citext) y el gate Ordinal NO la matchea → 404 (cierra el self-upgrade por <c>PABLO@…</c>).
///     Se usa una base DISTINTA de <see cref="CaseBaseAllowlisted"/> justamente porque bajo citext
///     una variante de caja de la MISMA base colisionaría con ella en el índice único.
///   • <see cref="SpaceBaseAllowlisted"/> — allowlisteado (nunca sembrado); <see cref="SpaceVariantAttacker"/>
///     (espacio inicial, fila distinta — citext no recorta espacios) NO matchea → 404.
/// Los demás emails del camino "permitido" son EXACTOS (uno por test → sin colisión con el índice
/// único de <c>users.email</c>). Un email interno NO listado (<see cref="NotAllowlistedInternal"/>)
/// se deja FUERA a propósito para probar que el gate es exacto, no por dominio.
/// </summary>
public sealed class DevTierOverrideEnabledFixture : ApiFixture
{
    public const string AllowedFlip = "flip@locallist.ai";
    public const string AllowedInvalidTier = "invalidtier@locallist.ai";
    public const string AllowedMissingTier = "missingtier@locallist.ai";
    public const string AllowedIgnoreBodyCaller = "ignorebody-caller@locallist.ai";
    public const string AllowedIgnoreBodyVictim = "ignorebody-victim@locallist.ai";
    public const string AllowedResetClear = "resetclear@locallist.ai";
    public const string AllowedResetScope = "resetscope@locallist.ai";

    // Control POSITIVO del BLOCKER de caja: base allowlisteada, sembrada en su misma caja → 200.
    public const string CaseBaseAllowlisted = "casebase@locallist.ai";

    // BLOCKER de caja: base allowlisteada (minúsculas) que NINGÚN test siembra como fila. El atacante
    // siembra la VARIANTE en mayúsculas → bajo citext, como la base no existe, la variante entra como
    // su propia fila (no colisiona con el índice único) y el gate Ordinal NO la matchea → 404. Base
    // separada de CaseBaseAllowlisted a propósito: una variante de caja de una base YA sembrada
    // chocaría con ella en el índice único citext (email case-insensitive).
    public const string CaseVariantBaseAllowlisted = "casevariant@locallist.ai";
    public const string CaseVariantAttacker = "CASEVARIANT@locallist.ai";

    // BLOCKER de espacios: la base allowlisteada (sin espacios). El atacante registra con un espacio
    // inicial → fila distinta (whitespace-sensitive) → NO matchea sin trim → 404.
    public const string SpaceBaseAllowlisted = "spacebase@locallist.ai";
    public const string SpaceVariantAttacker = " spacebase@locallist.ai";

    // BLOCKER de caja sobre reset-quota (base propia para no compartir usuario con el flip test).
    public const string ResetCaseBaseAllowlisted = "resetcasebase@locallist.ai";
    public const string ResetCaseVariantAttacker = "RESETCASEBASE@locallist.ai";

    // Interno (@locallist.ai) pero deliberadamente AUSENTE del allowlist → debe dar 404 (prueba de
    // que el gate es EXACTO, no por dominio; es el objetivo de la mutación del check de allowlist).
    public const string NotAllowlistedInternal = "notlisted@locallist.ai";

    // Parecidos a AllowedFlip pero NO exactos → no matchean.
    public const string SubdomainLookalike = "flip@locallist.ai.evil.com";
    public const string PrefixLookalike = "xflip@locallist.ai";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Dev:TierOverrideEnabled", "true");
        var allowlist = new[]
        {
            AllowedFlip, AllowedInvalidTier, AllowedMissingTier,
            AllowedIgnoreBodyCaller, AllowedIgnoreBodyVictim,
            AllowedResetClear, AllowedResetScope,
            CaseBaseAllowlisted, CaseVariantBaseAllowlisted, SpaceBaseAllowlisted, ResetCaseBaseAllowlisted,
        };
        for (var i = 0; i < allowlist.Length; i++)
            builder.UseSetting($"Dev:AllowedEmails:{i}", allowlist[i]);
        base.ConfigureWebHost(builder);
    }
}

/// <summary>
/// Fixture con el flag ON pero el allowlist VACÍO (no se setea <c>Dev:AllowedEmails</c>). Modela el
/// SEGUNDO fail-closed: aunque el override esté encendido, sin allowlist NADIE pasa → 404. Container
/// propio.
/// </summary>
public sealed class DevTierFlagOnEmptyAllowlistFixture : ApiFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Dev:TierOverrideEnabled", "true");
        // Dev:AllowedEmails deliberadamente SIN setear → vacío (default) → fail-closed.
        base.ConfigureWebHost(builder);
    }
}

/// <summary>
/// Segundo fail-closed: flag ON + allowlist VACÍO → 404 para cualquier email (incluso interno), y sin
/// efecto. Mutación: si el gate tratara el allowlist vacío como "todos permitidos", esto fliparía el
/// tier / borraría cuotas y el test caería.
/// </summary>
public class AccountDevTierEmptyAllowlistTests(DevTierFlagOnEmptyAllowlistFixture fixture)
    : IClassFixture<DevTierFlagOnEmptyAllowlistFixture>
{
    [Fact]
    public async Task FlagOn_EmptyAllowlist_InternalEmail_Returns404_NoFlip()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"empty-allow-{uid:N}@locallist.ai", tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        var db = fixture.GetDbContext();
        var user = await db.Users.FirstAsync(u => u.Id == uid);
        Assert.Equal("free", user.Tier);
    }

    [Fact]
    public async Task FlagOn_EmptyAllowlist_ResetQuota_Returns404_NoDelete()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"empty-allow-reset-{uid:N}@locallist.ai", tier: "free");
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
/// F-dev — <c>POST /account/dev/{tier,reset-quota}</c> sobre DB real (ApiFixture = Testcontainers
/// PostgreSQL) con el flag ENCENDIDO y un allowlist EXACTO. Verifica el doble gate FAIL-CLOSED:
/// email EN el allowlist → aplica el efecto en la DB (no-vacuo: se relee la fila); email NO listado
/// (aunque sea @locallist.ai) o parecido-pero-no-exacto → 404 opaco; tier inválido → 400; y que el
/// efecto va SIEMPRE al usuario del token (jamás a un id del body). El match es case-insensitive +
/// trim. El 404 con flag apagado vive en <see cref="AccountDevTierDisabledTests"/>; el 404 con
/// allowlist vacío en <see cref="AccountDevTierEmptyAllowlistTests"/>.
/// </summary>
public class AccountDevTierTests(DevTierOverrideEnabledFixture fixture)
    : IClassFixture<DevTierOverrideEnabledFixture>
{
    // Id estable derivado del email → repetidos seeds del MISMO email reutilizan el mismo usuario
    // (CreateAppAuthenticatedClientWithUser hace FindAsync(uid) y no re-inserta), evitando chocar con
    // el índice único de users.email cuando un [Theory] repite invocaciones sobre el mismo email.
    private static Guid StableId(string email)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(email));
        return new Guid(hash);
    }

    private static async Task<string> TierInDb(ApiFixture f, Guid userId)
    {
        var db = f.GetDbContext();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        return user.Tier;
    }

    [Fact]
    public async Task FlagOn_AllowlistedEmail_FlipsTierInDb_ProThenFree()
    {
        var uid = StableId(DevTierOverrideEnabledFixture.AllowedFlip);
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, DevTierOverrideEnabledFixture.AllowedFlip, tier: "free");

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
    public async Task FlagOn_ExactSameCaseAllowlistedEmail_Flips()
    {
        // La cuenta con el email allowlisteado en su MISMA caja (Ordinal exact) → 200 y flip. Es el
        // control positivo del BLOCKER: el fix cierra las variantes sin romper el match legítimo.
        var uid = StableId(DevTierOverrideEnabledFixture.CaseBaseAllowlisted);
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, DevTierOverrideEnabledFixture.CaseBaseAllowlisted, tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("pro", await TierInDb(fixture, uid));
    }

    [Fact]
    public async Task FlagOn_CaseVariantOfAllowlistedEmail_Returns404_NoFlip()
    {
        // BLOCKER: el allowlist trae "casevariant@locallist.ai" (minúsculas, NUNCA sembrado). El
        // atacante siembra la variante en mayúsculas ("CASEVARIANT@locallist.ai"): bajo citext, como
        // la base no se materializa, entra como fila propia y obtiene token AppScheme. El gate Ordinal
        // NO lo matchea → 404, sin self-upgrade. MUTACIÓN: volver a OrdinalIgnoreCase → 200.
        // (Nota: citext + normalización ya impedirían crear la variante junto a su base; el gate
        // Ordinal es la defensa-en-profundidad que además cubre datos legados sin normalizar.)
        var uid = StableId(DevTierOverrideEnabledFixture.CaseVariantAttacker);
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, DevTierOverrideEnabledFixture.CaseVariantAttacker, tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("free", await TierInDb(fixture, uid)); // sin efecto: bypass cerrado
    }

    [Fact]
    public async Task FlagOn_WhitespaceVariantOfAllowlistedEmail_Returns404_NoFlip()
    {
        // Misma clase de bypass por espacios: el allowlist trae "spacebase@locallist.ai"; el atacante
        // registra " spacebase@locallist.ai" (espacio inicial), fila distinta (whitespace-sensitive).
        // El gate NO hace trim → no matchea → 404. MUTACIÓN: reintroducir Trim() → 200.
        var uid = StableId(DevTierOverrideEnabledFixture.SpaceVariantAttacker);
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, DevTierOverrideEnabledFixture.SpaceVariantAttacker, tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("free", await TierInDb(fixture, uid));
    }

    [Fact]
    public async Task ResetQuota_FlagOn_CaseVariantOfAllowlistedEmail_Returns404_NoDelete()
    {
        // El BLOCKER también sobre reset-quota (gates compartidos): el allowlist trae
        // "resetcasebase@locallist.ai" pero el atacante registra "RESETCASEBASE@locallist.ai" (otra
        // caja, fila distinta) → 404 y las cuotas NO se borran. MUTACIÓN: OrdinalIgnoreCase → borra.
        var uid = StableId(DevTierOverrideEnabledFixture.ResetCaseVariantAttacker);
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, DevTierOverrideEnabledFixture.ResetCaseVariantAttacker, tier: "free");
        await SeedCounter(fixture, uid, PlanGenerationGateService.FeatureMonthly, 3);

        var res = await client.PostAsJsonAsync("/account/dev/reset-quota", new { });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(1, await CounterRows(fixture, uid)); // gate cortó antes del DELETE
    }

    [Fact]
    public async Task FlagOn_InternalDomainButNotAllowlisted_Returns404_NoFlip()
    {
        // Email @locallist.ai pero AUSENTE del allowlist → 404. Prueba clave de que el gate es
        // EXACTO, no por dominio (el viejo gate de dominio SÍ lo habría dejado pasar).
        // MUTACIÓN del check de allowlist: quitar IsAllowedEmail → esto pasa a 200 y flipa el tier.
        var uid = StableId(DevTierOverrideEnabledFixture.NotAllowlistedInternal);
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, DevTierOverrideEnabledFixture.NotAllowlistedInternal, tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("free", await TierInDb(fixture, uid)); // sin efecto en la DB
    }

    [Theory]
    [InlineData(DevTierOverrideEnabledFixture.SubdomainLookalike)]
    [InlineData(DevTierOverrideEnabledFixture.PrefixLookalike)]
    public async Task FlagOn_SimilarButNotExactEmail_Returns404(string email)
    {
        // Parecidos a "flip@locallist.ai" (subdominio evil / prefijo) pero NO exactos → no matchean.
        var uid = StableId(email);
        var client = await fixture.CreateAppAuthenticatedClientWithUser(uid, email, tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("free", await TierInDb(fixture, uid));
    }

    [Fact]
    public async Task FlagOn_NonInternalEmail_Returns404()
    {
        // Un email totalmente ajeno (gmail) tampoco está en el allowlist → 404.
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"outsider-{uid:N}@gmail.com", tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("free", await TierInDb(fixture, uid));
    }

    [Theory]
    [InlineData("plus")]
    [InlineData("PRO")]
    [InlineData("")]
    [InlineData("admin")]
    public async Task FlagOn_AllowlistedEmail_InvalidTier_Returns400(string badTier)
    {
        // Gate 3: tier estricto {pro,free}. Otro valor → 400 (caller ya validado por gates 1+2).
        var uid = StableId(DevTierOverrideEnabledFixture.AllowedInvalidTier);
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, DevTierOverrideEnabledFixture.AllowedInvalidTier, tier: "free");

        var res = await client.PostAsJsonAsync("/account/dev/tier", new { tier = badTier });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("free", await TierInDb(fixture, uid)); // sin cambio de tier
    }

    [Fact]
    public async Task FlagOn_AllowlistedEmail_MissingTier_Returns400()
    {
        var uid = StableId(DevTierOverrideEnabledFixture.AllowedMissingTier);
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, DevTierOverrideEnabledFixture.AllowedMissingTier, tier: "free");

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
        // El efecto va SIEMPRE al usuario del token, NUNCA a un id del body. Ambos (caller y víctima)
        // están allowlisteados para aislar el punto bajo prueba (el gate de email no es lo que salva a
        // la víctima aquí): mandamos el id de la víctima en el body y NO debe cambiar; el caller SÍ.
        var callerId = StableId(DevTierOverrideEnabledFixture.AllowedIgnoreBodyCaller);
        var victimId = StableId(DevTierOverrideEnabledFixture.AllowedIgnoreBodyVictim);

        var db = fixture.GetDbContext();
        db.Users.Add(new User
        {
            Id = victimId,
            Email = DevTierOverrideEnabledFixture.AllowedIgnoreBodyVictim,
            FirebaseUid = "app-" + victimId,
            Tier = "free",
        });
        await db.SaveChangesAsync();

        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            callerId, DevTierOverrideEnabledFixture.AllowedIgnoreBodyCaller, tier: "free");

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
    public async Task ResetQuota_FlagOn_AllowlistedEmail_ClearsCallerCounters()
    {
        var uid = StableId(DevTierOverrideEnabledFixture.AllowedResetClear);
        // El usuario debe existir antes de sembrar usage_counters (FK user_id).
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, DevTierOverrideEnabledFixture.AllowedResetClear, tier: "free");

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
        // El borrado es por el id del TOKEN: la cuota de otro usuario NO se toca. El "otro" no
        // necesita estar allowlisteado (nunca es el caller); usamos un email interno cualquiera.
        var callerId = StableId(DevTierOverrideEnabledFixture.AllowedResetScope);
        var otherId = Guid.NewGuid();

        // Ambos usuarios deben existir antes de sembrar sus counters (FK user_id).
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            callerId, DevTierOverrideEnabledFixture.AllowedResetScope, tier: "free");
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
    public async Task ResetQuota_FlagOn_NotAllowlistedInternalEmail_Returns404_NoDelete()
    {
        // Interno pero no allowlisteado → 404 y las cuotas NO se borran (gate exacto, no dominio).
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            uid, $"reset-notlisted-{uid:N}@locallist.ai", tier: "free");
        await SeedCounter(fixture, uid, PlanGenerationGateService.FeatureMonthly, 3);

        var res = await client.PostAsJsonAsync("/account/dev/reset-quota", new { });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(1, await CounterRows(fixture, uid)); // gate de allowlist cortó antes del DELETE
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
/// ausente → false (y allowlist vacío). Con el flag apagado el endpoint es 404 OPACO para TODOS,
/// incluido un usuario @locallist.ai legítimo. Mutación: si se quita el check del flag, este 404
/// pasa a 200 (o 400/OK) y el test cae — es la prueba de que con el flag off el override no existe.
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
