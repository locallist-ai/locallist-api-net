using System.Net;
using System.Text;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LocalList.API.Tests.Features;

/// <summary>
/// F4 — gates server-side del catálogo Plus sobre la generación de planes IA
/// (<c>POST /builder/chat</c> y <c>POST /chat/generate</c>), sobre DB real (ApiFixture =
/// Testcontainers PostgreSQL):
///
///   - Auth requerida (401 anónimo) en ambos endpoints.
///   - Contador mensual free (3/mes): 4º plan → 403 estructurado; periodos independientes;
///     rollover de mes (FakeTime); increment ATÓMICO bajo concurrencia (servicio y endpoint).
///   - Cap antiabuso Plus (50/día) → 429 estructurado (no 403: un Plus no debe ver upsell).
///   - Duración por tier: free ≤ 3 (403 duration_requires_plus), hard cap 14 para todos (400),
///     y clamp del pipeline sobre días derivados por el LLM del texto libre (+ hint `clamped`).
///   - Cupo de planes guardados (5 free): NO gatea la generación (decisión Pablo 2026-07-22);
///     su enforcement vive en POST /plans (ver PlansTests). Aquí solo se prueba la independencia.
///   - Tier SIEMPRE fresco de DB: un claim "pro" forjado en el JWT no abre nada.
///   - Semántica del contador: NO se consume si un gate rechaza antes de generar; SÍ se
///     consume cuando la generación arranca aunque no produzca plan (sin places).
///
/// NOTA (huecos documentados, ver PlanGenerationGateService): favoritos no tiene modelo
/// backend todavía (límite 50 free pendiente de ese modelo) y multi-ciudad es imposible por
/// construcción (request mono-ciudad), así que no hay gate que testear para ninguno de los dos.
/// </summary>
public class PlanGateTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    private const string Miami = "Miami";

    public void Dispose()
    {
        fixture.FakeGemini.Responder = null;
        fixture.FakeGemini.Calls.Clear();
    }

    // ── Auth requerida ────────────────────────────────────────────────────────

    [Fact]
    public async Task BuilderChat_Anonymous_Returns401()
    {
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/builder/chat", BuilderBody(IsolatedCity("anon"), 1));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ChatGenerate_Anonymous_Returns401()
    {
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/generate", new { sessionId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── Contador mensual free (3/mes) ────────────────────────────────────────

    [Fact]
    public async Task Builder_FreeUser_FourthPlanOfMonth_Returns403Structured()
    {
        var city = IsolatedCity("month");
        await SeedPlaces(city, 4);
        SetGeminiExtraction(days: 1);

        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"free-month-{userId:N}@test.com");

        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/builder/chat", BuilderBody(city, 1));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var res = await client.PostAsJsonAsync("/builder/chat", BuilderBody(city, 1));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("plan_limit_reached", body.GetProperty("error").GetString());
        Assert.Equal(3, body.GetProperty("used").GetInt32());
        Assert.Equal(3, body.GetProperty("limit").GetInt32());

        // resetsAt = primer día del mes siguiente, 00:00 UTC (según el reloj fake del fixture).
        var now = fixture.FakeTime.GetUtcNow();
        var expectedReset = new DateTimeOffset(
            new DateOnly(now.Year, now.Month, 1).AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        Assert.Equal(expectedReset, body.GetProperty("resetsAt").GetDateTimeOffset());

        // El contador quedó exactamente en el límite — el 4º intento no lo movió.
        Assert.Equal(3, await MonthlyCount(userId));
    }

    [Fact]
    public async Task Builder_FreeUser_PreviousMonthExhausted_CurrentMonthStillWorks()
    {
        var city = IsolatedCity("prev");
        await SeedPlaces(city, 3);
        SetGeminiExtraction(days: 1);

        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"free-prev-{userId:N}@test.com");

        // Mes ANTERIOR agotado — el periodo es parte de la clave, no debe afectar al actual.
        var now = fixture.FakeTime.GetUtcNow();
        var db = fixture.GetDbContext();
        db.UsageCounters.Add(new UsageCounter
        {
            UserId = userId,
            Feature = PlanGenerationGateService.FeatureMonthly,
            PeriodStart = new DateOnly(now.Year, now.Month, 1).AddMonths(-1),
            Count = PlanGenerationGateService.FreeMonthlyPlanLimit,
        });
        await db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync("/builder/chat", BuilderBody(city, 1));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, await MonthlyCount(userId));
    }

    // (El rollover de mes con avance de reloj vive en PlanGateMonthRolloverTests, con
    // fixture PROPIO: FakeTimeProvider no puede retroceder, y avanzar 35 días el reloj
    // compartido invalidaría el nbf de todos los tokens posteriores de esta clase.)

    // ── Duración por tier ────────────────────────────────────────────────────

    [Fact]
    public async Task Builder_FreeUser_FourDays_Returns403DurationRequiresPlus_NoCounterConsumed()
    {
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"free-dur-{userId:N}@test.com");

        var res = await client.PostAsJsonAsync("/builder/chat", BuilderBody(IsolatedCity("dur4"), 4));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("duration_requires_plus", body.GetProperty("error").GetString());
        Assert.Equal(4, body.GetProperty("requestedDays").GetInt32());
        Assert.Equal(3, body.GetProperty("maxDays").GetInt32());
        Assert.Equal(14, body.GetProperty("plusMaxDays").GetInt32());

        // Rechazo ANTES de arrancar la generación → contador intacto.
        Assert.Equal(0, await MonthlyCount(userId));
    }

    [Fact]
    public async Task Builder_FreeUser_ThreeDays_Ok()
    {
        var city = IsolatedCity("dur3");
        await SeedPlaces(city, 4);
        SetGeminiExtraction(days: 3);

        var client = await fixture.CreateGenerationClientAsync(tier: "free");
        var res = await client.PostAsJsonAsync("/builder/chat", BuilderBody(city, 3));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Builder_ForgedProClaimInJwt_DbSaysFree_GateStillDenies()
    {
        // El claim tier del JWT es forjable/rancio: el gate relee SIEMPRE la DB.
        var userId = Guid.NewGuid();
        var email = $"forged-{userId:N}@test.com";
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = email, Tier = "free" });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", fixture.CreateAppToken(userId, email, tier: "pro")); // ← claim forjado

        var res = await client.PostAsJsonAsync("/builder/chat", BuilderBody(IsolatedCity("forged"), 5));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("duration_requires_plus", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Builder_ProUser_FourteenDays_Ok_FifteenDays_Returns400()
    {
        var city = IsolatedCity("pro14");
        await SeedPlaces(city, 6);
        SetGeminiExtraction(days: 14);

        var client = await fixture.CreateGenerationClientAsync(tier: "pro");

        var ok = await client.PostAsJsonAsync("/builder/chat", BuilderBody(city, 14));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(14, body.GetProperty("plan").GetProperty("durationDays").GetInt32());

        // Hard cap del catálogo: > 14 es inválido para TODOS (validación del DTO → 400).
        var too = await client.PostAsJsonAsync("/builder/chat", BuilderBody(city, 15));
        Assert.Equal(HttpStatusCode.BadRequest, too.StatusCode);
    }

    [Fact]
    public async Task Builder_FreeUser_FifteenDays_Returns400_HardCapForEveryone()
    {
        var client = await fixture.CreateGenerationClientAsync(tier: "free");
        var res = await client.PostAsJsonAsync("/builder/chat", BuilderBody(IsolatedCity("free15"), 15));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Builder_LlmDerivedDays_ClampedToTierCap()
    {
        // El gate solo ve los días EXPLÍCITOS del request; si el LLM deriva días del texto
        // libre, el clamp del pipeline (PlanGenerationService) los acota al techo del tier.
        var city = IsolatedCity("clamp");
        await SeedPlaces(city, 6);
        SetGeminiExtraction(days: 10); // el LLM "alucina" 10 días

        // Free (sin days en el request → el gate no rechaza): plan acotado a 3 días.
        var freeClient = await fixture.CreateGenerationClientAsync(tier: "free");
        var freeRes = await freeClient.PostAsJsonAsync("/builder/chat", BuilderBody(city, days: null));
        Assert.Equal(HttpStatusCode.OK, freeRes.StatusCode);
        var freeBody = await freeRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, freeBody.GetProperty("plan").GetProperty("durationDays").GetInt32());

        // m3/F6: el clamp silencioso emite un hint estructurado para el upsell de la app.
        var clamped = freeBody.GetProperty("clamped");
        Assert.Equal(JsonValueKind.Object, clamped.ValueKind);
        Assert.Equal("days", clamped.GetProperty("field").GetString());
        Assert.Equal(10, clamped.GetProperty("requested").GetInt32());
        Assert.Equal(3, clamped.GetProperty("applied").GetInt32());
        Assert.True(clamped.GetProperty("upsell").GetBoolean());

        // Pro: 10 ≤ 14 → sin clamp → clamped null (nada que upsellear).
        var proClient = await fixture.CreateGenerationClientAsync(tier: "pro");
        var proRes = await proClient.PostAsJsonAsync("/builder/chat", BuilderBody(city, days: null));
        Assert.Equal(HttpStatusCode.OK, proRes.StatusCode);
        var proBody = await proRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, proBody.GetProperty("plan").GetProperty("durationDays").GetInt32());
        // Sin clamp → hint omitido (la API serializa con WhenWritingNull); la app trata la
        // ausencia de `clamped` como "no hubo recorte".
        Assert.False(proBody.TryGetProperty("clamped", out _));
    }

    // ── F2: el cupo de planes guardados NO gatea la generación (límites independientes) ──
    // La ENFORCEMENT del cupo de 5 vive ahora en POST /plans (ver PlansTests). Aquí solo
    // verificamos la INDEPENDENCIA: la generación no se ve afectada por cuántos planes
    // guardados tenga el usuario.

    [Fact]
    public async Task Builder_FreeUser_FiveSavedPlans_StillGenerates_SavedQuotaIndependentFromMonthly()
    {
        // Decisión Pablo 2026-07-22: un free con 5 planes manuales sigue pudiendo consumir sus
        // 3 planes IA/mes — el cupo de guardados y el contador mensual son límites separados.
        var city = IsolatedCity("saved-indep");
        await SeedPlaces(city, 4);
        SetGeminiExtraction(days: 1);

        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"free-savedindep-{userId:N}@test.com");
        await SeedSavedPlans(userId, 5);

        var res = await client.PostAsJsonAsync("/builder/chat", BuilderBody(city, 1));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // La generación consumió el contador mensual (no la bloqueó el cupo de guardados).
        Assert.Equal(1, await MonthlyCount(userId));
    }

    // ── Plus: mensual ilimitado + cap antiabuso 50/día ───────────────────────

    [Fact]
    public async Task Builder_ProUser_FourPlansSameMonth_AllOk_DailyCounterTracks()
    {
        var city = IsolatedCity("pro4");
        await SeedPlaces(city, 4);
        SetGeminiExtraction(days: 1);

        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            userId, $"pro-month-{userId:N}@test.com", tier: "pro");

        for (var i = 0; i < 4; i++)
        {
            var res = await client.PostAsJsonAsync("/builder/chat", BuilderBody(city, 1));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        // Se lleva en el contador DIARIO; el mensual free no se toca.
        Assert.Equal(4, await DailyCount(userId));
        Assert.Equal(0, await MonthlyCount(userId));
    }

    [Fact]
    public async Task Builder_ProUser_DailyCapReached_Returns429Structured()
    {
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            userId, $"pro-cap-{userId:N}@test.com", tier: "pro");

        var now = fixture.FakeTime.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var db = fixture.GetDbContext();
        db.UsageCounters.Add(new UsageCounter
        {
            UserId = userId,
            Feature = PlanGenerationGateService.FeatureDaily,
            PeriodStart = today,
            Count = PlanGenerationGateService.PlusDailyPlanCap,
        });
        await db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync("/builder/chat", BuilderBody(IsolatedCity("cap"), 1));

        // 429, no 403: es throttling antiabuso, la app no debe pintar upsell a un Plus.
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("daily_cap_reached", body.GetProperty("error").GetString());
        Assert.Equal(50, body.GetProperty("used").GetInt32());
        Assert.Equal(50, body.GetProperty("limit").GetInt32());
        var expectedReset = new DateTimeOffset(
            today.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        Assert.Equal(expectedReset, body.GetProperty("resetsAt").GetDateTimeOffset());
    }

    // ── Semántica del contador: se consume al ARRANCAR la generación ─────────

    [Fact]
    public async Task Builder_GenerationStartsButNoPlaces_CounterStillConsumed()
    {
        // Ciudad sin places: el pipeline arranca (LLM + retrieval) y no produce plan
        // (soft-fallback 200 con warning). El permiso YA se gastó — semántica elegida:
        // el coste se pagó y devolverlo abriría retry-abuse barato del límite mensual.
        SetGeminiExtraction(days: 1);
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"free-cons-{userId:N}@test.com");

        var res = await client.PostAsJsonAsync("/builder/chat", BuilderBody(IsolatedCity("noplaces"), 1));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var warnings = body.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
        Assert.Contains("no_places_available", warnings);

        Assert.Equal(1, await MonthlyCount(userId));
    }

    [Fact]
    public async Task ChatGenerate_NoPlaces_Returns404_CounterStillConsumed()
    {
        // Miami está en la allowlist de cobertura pero este fixture no siembra places de
        // Miami → GenerateAsync devuelve null → 404 no_places_available. La generación
        // ARRANCÓ, así que el permiso queda consumido (misma semántica que arriba).
        SetGeminiExtraction(days: 1);
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"free-chat404-{userId:N}@test.com");
        var session = await SeedReadySession(userId, Miami, days: 1);

        var res = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_places_available", body.GetProperty("error").GetString());

        Assert.Equal(1, await MonthlyCount(userId));
    }

    // ── /chat/generate — gates propios ───────────────────────────────────────

    [Fact]
    public async Task ChatGenerate_FreeUser_SessionWithFiveDays_Returns403_NoConsume()
    {
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"free-chat5d-{userId:N}@test.com");
        var session = await SeedReadySession(userId, Miami, days: 5);

        var res = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("duration_requires_plus", body.GetProperty("error").GetString());

        Assert.Equal(0, await MonthlyCount(userId));
    }

    [Fact]
    public async Task ChatGenerate_Idempotent_ExhaustedCounter_StillReturnsExistingPlan()
    {
        // Releer un plan ya generado NO es generar: el path idempotente va ANTES del gate,
        // así que funciona incluso con el contador mensual agotado.
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"free-idem-{userId:N}@test.com");

        var db = fixture.GetDbContext();
        var plan = new Plan { Name = "Idem Plan", City = Miami, Type = "ai", DurationDays = 1, CreatedById = userId };
        db.Plans.Add(plan);
        var session = new ChatSession
        {
            UserId = userId,
            Status = "generated",
            GeneratedPlanId = plan.Id,
            TurnCount = 3,
            SlotsJson = JsonSerializer.Serialize(new LocalList.API.NET.Features.Chat.ChatSlots
            {
                City = Miami, Days = 1, GroupType = "couple",
                Categories = new() { "food" }, Budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);
        var now = fixture.FakeTime.GetUtcNow();
        db.UsageCounters.Add(new UsageCounter
        {
            UserId = userId,
            Feature = PlanGenerationGateService.FeatureMonthly,
            PeriodStart = new DateOnly(now.Year, now.Month, 1),
            Count = PlanGenerationGateService.FreeMonthlyPlanLimit, // agotado
        });
        await db.SaveChangesAsync();

        var res = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isExisting").GetBoolean());

        // Y el contador no se movió.
        Assert.Equal(3, await MonthlyCount(userId));
    }

    // ── Concurrencia — el increment es atómico ───────────────────────────────

    [Fact]
    public async Task UsageCounterService_TwentyParallelConsumes_ExactlyLimitSucceed()
    {
        var userId = await SeedUser("conc-svc");
        var period = new DateOnly(2026, 7, 1);

        // 20 tareas concurrentes, cada una con su scope/DbContext propio (como 20 requests).
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var scope = fixture.Services.CreateScope();
            var counters = scope.ServiceProvider.GetRequiredService<IUsageCounterService>();
            return await counters.TryConsumeAsync(userId, "conc_test", period, 3, CancellationToken.None);
        }));

        Assert.Equal(3, results.Count(r => r));
        Assert.Equal(17, results.Count(r => !r));

        var db = fixture.GetDbContext();
        var row = await db.UsageCounters.SingleAsync(
            uc => uc.UserId == userId && uc.Feature == "conc_test" && uc.PeriodStart == period);
        Assert.Equal(3, row.Count);
    }

    [Fact]
    public async Task Builder_FreeUser_SixParallelRequests_OnlyThreeGenerate()
    {
        // Criterio de aceptación: NINGÚN interleaving permite el 4º plan del mes. Seis
        // requests simultáneas del mismo free user → exactamente 3 generan (200) y 3
        // reciben plan_limit_reached; el contador queda en 3, no más.
        var city = IsolatedCity("conc-http");
        await SeedPlaces(city, 4);
        SetGeminiExtraction(days: 1);

        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"free-conc-{userId:N}@test.com");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            client.PostAsJsonAsync("/builder/chat", BuilderBody(city, 1))));

        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var deniedCount = responses.Count(r => r.StatusCode == HttpStatusCode.Forbidden);
        Assert.Equal(3, okCount);
        Assert.Equal(3, deniedCount);

        foreach (var denied in responses.Where(r => r.StatusCode == HttpStatusCode.Forbidden))
        {
            var body = await denied.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("plan_limit_reached", body.GetProperty("error").GetString());
        }

        Assert.Equal(3, await MonthlyCount(userId));

        // Y solo se persistieron 3 planes para el user.
        var db = fixture.GetDbContext();
        Assert.Equal(3, await db.Plans.CountAsync(p => p.CreatedById == userId));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string IsolatedCity(string tag)
    {
        var city = $"GateCity-{tag}-{Guid.NewGuid():N}"[..30];
        // Desde m1/F4 /builder/chat rechaza ciudades no cubiertas ANTES del gate; las ciudades
        // aisladas representan "una ciudad cubierta con catálogo fresco", así que las marcamos live.
        fixture.MarkCityLive(city);
        return city;
    }

    private static object BuilderBody(string city, int? days) => new
    {
        message = "plan de comida",
        tripContext = new { city, days, groupType = "couple", categories = new[] { "food" } },
    };

    private void SetGeminiExtraction(int days) =>
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Gate Test Plan",
            maxStopsPerDay = 3,
        }));

    private static HttpResponseMessage GeminiOk(string embeddedText)
    {
        var envelope = new
        {
            candidates = new[]
            {
                new { content = new { parts = new[] { new { text = embeddedText } } } }
            }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };
    }

    private async Task<Guid> SeedUser(string tag)
    {
        var userId = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = $"{tag}-{userId:N}@test.com" });
        await db.SaveChangesAsync();
        return userId;
    }

    private async Task SeedPlaces(string city, int count)
    {
        var db = fixture.GetDbContext();
        var tag = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < count; i++)
        {
            db.Places.Add(new Place
            {
                Id = Guid.NewGuid(),
                Name = $"Gate Place {tag}-{i}",
                Category = "food",
                City = city,
                WhyThisPlace = "Seeded for plan gate tests",
                Status = "published",
                BestTimes = new List<string> { "any" },
                Latitude = 25.77m + (decimal)(i * 0.01),
                Longitude = -80.19m + (decimal)(i * 0.01),
                GooglePlaceId = $"gpid-gate-{tag}-{i}",
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task SeedSavedPlans(Guid userId, int count)
    {
        var db = fixture.GetDbContext();
        for (var i = 0; i < count; i++)
        {
            db.Plans.Add(new Plan
            {
                Name = $"Saved {i}",
                City = Miami,
                Type = "ai",
                DurationDays = 1,
                IsPublic = false,
                CreatedById = userId,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<ChatSession> SeedReadySession(Guid userId, string city, int days)
    {
        var db = fixture.GetDbContext();
        var session = new ChatSession
        {
            UserId = userId,
            Status = "ready",
            TurnCount = 3,
            SlotsJson = JsonSerializer.Serialize(new LocalList.API.NET.Features.Chat.ChatSlots
            {
                City = city, Days = days, GroupType = "couple",
                Categories = new() { "food" }, Budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private async Task<int> MonthlyCount(Guid userId)
    {
        var now = fixture.FakeTime.GetUtcNow();
        var monthStart = new DateOnly(now.Year, now.Month, 1);
        var db = fixture.GetDbContext();
        return await db.UsageCounters
            .Where(uc => uc.UserId == userId
                         && uc.Feature == PlanGenerationGateService.FeatureMonthly
                         && uc.PeriodStart == monthStart)
            .Select(uc => uc.Count)
            .FirstOrDefaultAsync();
    }

    private async Task<int> DailyCount(Guid userId)
    {
        var now = fixture.FakeTime.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var db = fixture.GetDbContext();
        return await db.UsageCounters
            .Where(uc => uc.UserId == userId
                         && uc.Feature == PlanGenerationGateService.FeatureDaily
                         && uc.PeriodStart == today)
            .Select(uc => uc.Count)
            .FirstOrDefaultAsync();
    }
}

/// <summary>
/// Rollover de mes del contador free, en clase PROPIA (fixture propio, sin restore):
/// <c>FakeTimeProvider.SetUtcNow</c> no admite retroceder, así que avanzar el reloj del
/// fixture COMPARTIDO de <see cref="PlanGateTests"/> dejaría el nbf de los tokens de los
/// demás tests un mes en el futuro (401 en cascada — visto en la primera ejecución).
/// A nivel servicio (sin JWT): el gate resuelve el periodo con TimeProvider.
/// </summary>
public class PlanGateMonthRolloverTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Gate_MonthRollover_FakeTimeAdvance_AllowsAgain()
    {
        var userId = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = $"rollover-{userId:N}@test.com" });
        await db.SaveChangesAsync();

        using var scope = fixture.Services.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<IPlanGenerationGateService>();

        for (var i = 0; i < 3; i++)
            Assert.True((await gate.CheckAndConsumeAsync(userId, 1, CancellationToken.None)).Allowed);

        var denied = await gate.CheckAndConsumeAsync(userId, 1, CancellationToken.None);
        Assert.False(denied.Allowed);
        Assert.Equal(403, denied.Rejection!.StatusCode);

        // Mes siguiente (reloj fake avanzado 35 días) → periodo nuevo → contador fresco.
        fixture.FakeTime.SetUtcNow(fixture.FakeTime.GetUtcNow().AddDays(35));
        var allowedAgain = await gate.CheckAndConsumeAsync(userId, 1, CancellationToken.None);
        Assert.True(allowedAgain.Allowed);

        // Y el mes agotado sigue intacto en su fila (periodos independientes).
        var counts = await fixture.GetDbContext().UsageCounters
            .Where(uc => uc.UserId == userId && uc.Feature == PlanGenerationGateService.FeatureMonthly)
            .OrderBy(uc => uc.PeriodStart)
            .Select(uc => uc.Count)
            .ToListAsync();
        Assert.Equal(new[] { 3, 1 }, counts);
    }
}
