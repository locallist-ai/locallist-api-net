using System.Text;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// API-3: persistencia y exposicion de <c>Plan.StartDate</c>.
///
/// API-1/API-2 (ya en main) hacen que GENERAR un plan con StartDate produzca un
/// itinerario viable el dia de la semana correcto. API-3 cierra el bucle en el
/// backend: el plan generado RECUERDA su fecha (columna <c>plans.start_date</c>,
/// tipo Postgres <c>date</c>, nullable) y los endpoints de lectura la exponen en
/// ISO <c>yyyy-MM-dd</c> para que la app muestre la fecha del viaje y derive la
/// fecha de cada dia (dia N = StartDate + (N-1)).
///
/// El round-trip pasa por Postgres real (Testcontainers via <see cref="ApiFixture"/>),
/// nunca por un mock: se genera el plan por HTTP, se relee por HTTP y por DbContext.
/// </summary>
public class PlanStartDatePersistenceTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    private const string Miami = "Miami";

    public void Dispose()
    {
        fixture.FakeGemini.Responder = null;
        fixture.FakeGemini.Calls.Clear();
    }

    // ── 1 + 3: creacion via /builder/chat (TripContextDto) persiste StartDate y el GET lo devuelve ──
    // Estos golpean BuilderController (/builder/chat), no ChatController (/chat/generate): el nombre
    // Builder_* refleja el path real que ejercitan (antes se llamaban Chat_*, enganoso).

    [Fact]
    public async Task Builder_AuthenticatedWithStartDate_PersistsAndGetReturnsSameValue()
    {
        await SeedPublishedMiamiPlaces(3);
        StubGeminiPrefs();

        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-sd-{userId:N}";
        var client = await fixture.CreateAuthenticatedClientWithUser(userId, firebaseUid, $"sd-{userId:N}@test.com");

        // Fecha dentro de la ventana valida del controller ([today-1, today+365]).
        // El controller usa DateTime.UtcNow real (no FakeTime), asi que la derivamos igual.
        var startDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);
        var startIso = startDate.ToString("yyyy-MM-dd");

        var createResponse = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "romantic food trip in Miami",
            tripContext = new { city = Miami, days = 1, groupType = "couple", startDate = startIso },
        });

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var createBody = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var planId = createBody.GetProperty("plan").GetProperty("id").GetGuid();

        // La respuesta de creacion ya expone la fecha persistida (entidad serializada).
        Assert.Equal(startIso, createBody.GetProperty("plan").GetProperty("startDate").GetString());

        // Round-trip real por Postgres: el GET la relee de la fila persistida.
        var getResponse = await client.GetAsync($"/plans/{planId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(startIso, getBody.GetProperty("startDate").GetString());

        // Y por DbContext directo, confirmando el tipo `date` de Postgres round-trips DateOnly.
        var db = fixture.GetDbContext();
        var persisted = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == planId);
        Assert.Equal(startDate, persisted.StartDate);
    }

    // ── 2: creacion via /builder/chat sin fecha => StartDate null, sin regresion, GET devuelve null ──

    [Fact]
    public async Task Builder_AuthenticatedWithoutStartDate_PersistsNull_GetOmitsStartDate()
    {
        await SeedPublishedMiamiPlaces(3);
        StubGeminiPrefs();

        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-nosd-{userId:N}";
        var client = await fixture.CreateAuthenticatedClientWithUser(userId, firebaseUid, $"nosd-{userId:N}@test.com");

        var createResponse = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "romantic food trip in Miami",
            tripContext = new { city = Miami, days = 1, groupType = "couple" }, // sin startDate
        });

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var createBody = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var planId = createBody.GetProperty("plan").GetProperty("id").GetGuid();

        // Persistida como null.
        var db = fixture.GetDbContext();
        var persisted = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == planId);
        Assert.Null(persisted.StartDate);

        // GET no debe romper. startDate ausente (WhenWritingNull) o explicitamente null.
        var getResponse = await client.GetAsync($"/plans/{planId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        AssertStartDateAbsentOrNull(getBody);
    }

    // ── MAJOR (repro permanente): /chat/generate autenticado persiste StartDate en la ──
    // ── columna dedicada Y coherente con el blob JSON TripContext, y el GET la devuelve. ──
    // Antes del fix el path /chat/generate serializaba la fecha en el JSON TripContext pero
    // dejaba plans.start_date en NULL (persistencia asimetrica vs BuilderController).

    [Fact]
    public async Task ChatGenerate_AuthenticatedWithStartDate_PersistsColumnCoherentWithTripContextJson()
    {
        await SeedPublishedMiamiPlaces(3);
        StubGeminiPrefs();

        var userId = Guid.NewGuid();
        var email = $"chatsd-{userId:N}@test.com";

        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = email, FirebaseUid = $"app-{userId}" });
        var session = new ChatSession
        {
            UserId = userId,
            Status = "ready",
            TurnCount = 3,
            SlotsJson = JsonSerializer.Serialize(new
            {
                city = Miami, days = 1, groupType = "couple",
                categories = new[] { "food" }, budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        var startDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);
        var startIso = startDate.ToString("yyyy-MM-dd");

        // Token de la APP (HS256/AppScheme): mismo helper que el resto de tests autenticados de
        // /chat/generate; garantiza que la sesion se resuelve por propietario (userId del token).
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", fixture.CreateAppToken(userId, email));

        var createResponse = await client.PostAsJsonAsync("/chat/generate", new
        {
            sessionId = session.Id,
            startDate = startIso,
        });

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var createBody = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var planId = createBody.GetProperty("plan").GetProperty("id").GetGuid();

        // La columna dedicada plans.start_date quedo persistida (antes del fix: NULL).
        var readDb = fixture.GetDbContext();
        var persisted = await readDb.Plans.AsNoTracking().FirstAsync(p => p.Id == planId);
        Assert.Equal(startDate, persisted.StartDate);

        // Coherencia total: el blob JSON TripContext lleva la MISMA fecha que la columna.
        Assert.NotNull(persisted.TripContext);
        Assert.Equal(startIso, GetStartDateFromTripContext(persisted.TripContext!));

        // Round-trip por HTTP: el GET la relee de la fila persistida.
        var getResponse = await client.GetAsync($"/plans/{planId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(startIso, getBody.GetProperty("startDate").GetString());
    }

    // ── /chat/generate sin fecha => StartDate null (compat), sin regresion ──

    [Fact]
    public async Task ChatGenerate_AuthenticatedWithoutStartDate_PersistsNull()
    {
        await SeedPublishedMiamiPlaces(3);
        StubGeminiPrefs();

        var userId = Guid.NewGuid();
        var email = $"chatnosd-{userId:N}@test.com";

        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = email, FirebaseUid = $"app-{userId}" });
        var session = new ChatSession
        {
            UserId = userId,
            Status = "ready",
            TurnCount = 3,
            SlotsJson = JsonSerializer.Serialize(new
            {
                city = Miami, days = 1, groupType = "couple",
                categories = new[] { "food" }, budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", fixture.CreateAppToken(userId, email));

        var createResponse = await client.PostAsJsonAsync("/chat/generate", new
        {
            sessionId = session.Id, // sin startDate
        });

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var createBody = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var planId = createBody.GetProperty("plan").GetProperty("id").GetGuid();

        var readDb = fixture.GetDbContext();
        var persisted = await readDb.Plans.AsNoTracking().FirstAsync(p => p.Id == planId);
        Assert.Null(persisted.StartDate);

        var getResponse = await client.GetAsync($"/plans/{planId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        AssertStartDateAbsentOrNull(getBody);
    }

    // ── 3: legacy plan (fila preexistente sin fecha) se relee null sin romper ──

    [Fact]
    public async Task GetPlan_LegacyPlanWithoutStartDate_ReturnsNull()
    {
        // Simula una fila anterior a la migracion: creada sin StartDate. Tras aplicar la
        // migracion (columna nullable, sin default), estas filas quedan con start_date NULL
        // y siguen leyendose sin regresion.
        var db = fixture.GetDbContext();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Legacy Plan",
            City = Miami,
            Type = "curated",
            IsPublic = true,
            IsShowcase = true,
            // StartDate no asignada => null
        };
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/plans/{plan.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertStartDateAbsentOrNull(body);
    }

    // ── 3: la migracion produjo una columna date nullable (segura sobre datos existentes) ──

    [Fact]
    public async Task Migration_StartDateColumn_IsNullableDate()
    {
        // Verifica el contrato de la migracion contra el catalogo real de Postgres:
        // columna `date` y is_nullable=YES => aplica limpia sobre filas existentes sin
        // NOT NULL ni backfill (los planes legacy quedan NULL).
        var db = fixture.GetDbContext();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT is_nullable, data_type FROM information_schema.columns " +
            "WHERE table_name = 'plans' AND column_name = 'start_date'";
        using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "La columna plans.start_date no existe tras migrar");
        Assert.Equal("YES", reader.GetString(0));  // is_nullable
        Assert.Equal("date", reader.GetString(1)); // data_type
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertStartDateAbsentOrNull(JsonElement body)
    {
        if (body.TryGetProperty("startDate", out var sd))
            Assert.Equal(JsonValueKind.Null, sd.ValueKind);
    }

    /// <summary>
    /// Extrae la fecha (ISO yyyy-MM-dd) del blob JSON TripContext persistido en la fila. Busca la
    /// propiedad StartDate sin depender del casing del serializador para no acoplarse a el.
    /// </summary>
    private static string? GetStartDateFromTripContext(JsonDocument tripContext)
    {
        foreach (var prop in tripContext.RootElement.EnumerateObject())
        {
            if (string.Equals(prop.Name, "startDate", StringComparison.OrdinalIgnoreCase))
                return prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetString();
        }
        return null;
    }

    private void StubGeminiPrefs()
    {
        var extracted = new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new[] { "romantic" },
            groupType = "couple",
            planName = "StartDate Test",
            maxStopsPerDay = 4,
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(extracted));
    }

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

    private async Task SeedPublishedMiamiPlaces(int count)
    {
        var db = fixture.GetDbContext();
        var tag = Guid.NewGuid().ToString("N")[..8];
        for (int i = 0; i < count; i++)
        {
            db.Places.Add(new Place
            {
                Id = Guid.NewGuid(),
                Name = $"StartDate Seed {tag}-{i}",
                Category = "Food",
                City = Miami,
                WhyThisPlace = "Seed para test de persistencia de StartDate",
                Status = "published",
                BestTimes = new List<string> { "any" },
                Latitude = 25.77m + (decimal)(i * 0.01),
                Longitude = -80.19m + (decimal)(i * 0.01),
                GooglePlaceId = $"gpid-sd-{tag}-{i}",
            });
        }
        await db.SaveChangesAsync();
    }
}
