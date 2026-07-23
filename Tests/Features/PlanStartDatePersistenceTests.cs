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

    // ── 1 + 3: creacion desde TripContextDto persiste StartDate y el GET lo devuelve ──

    [Fact]
    public async Task Chat_AuthenticatedWithStartDate_PersistsAndGetReturnsSameValue()
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

    // ── 2: creacion sin fecha => StartDate null, sin regresion, GET devuelve null ──

    [Fact]
    public async Task Chat_AuthenticatedWithoutStartDate_PersistsNull_GetOmitsStartDate()
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
