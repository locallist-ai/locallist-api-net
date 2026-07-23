using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

public class PlansTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task GetPlans_Anonymous_ReturnsShowcaseOnly()
    {
        var db = fixture.GetDbContext();
        db.Plans.Add(MakePlan("Showcase Only", isShowcase: true));
        db.Plans.Add(MakePlan("Hidden Non-Showcase", isShowcase: false));
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync("/plans");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var p in body.GetProperty("plans").EnumerateArray())
            Assert.True(p.GetProperty("isShowcase").GetBoolean());
    }

    [Fact]
    public async Task GetPlans_Authenticated_ReturnsAllPublic()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-plans-{userId:N}";
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = $"plans-auth-{userId:N}@test.com", FirebaseUid = firebaseUid });
        var nonShowcase = MakePlan("Auth Visible", isShowcase: false);
        db.Plans.Add(nonShowcase);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(userId, firebaseUid);
        var response = await client.GetAsync("/plans");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("plans").EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(nonShowcase.Id, ids);
    }

    [Fact]
    public async Task GetPlan_ReturnsWithStops()
    {
        var db = fixture.GetDbContext();
        var place = new Place
        {
            Id = Guid.NewGuid(), Name = "Stop Place", Category = "Food",
            WhyThisPlace = "Amazing", Status = "published"
        };
        var plan = MakePlan("Plan With Stops");
        db.Places.Add(place);
        db.Plans.Add(plan);
        db.PlanStops.Add(new PlanStop
        {
            Id = Guid.NewGuid(), PlanId = plan.Id, PlaceId = place.Id,
            DayNumber = 1, OrderIndex = 0, TimeBlock = "morning"
        });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/plans/{plan.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Plan With Stops", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("days").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetPlan_PrivatePlan_OtherUser_Returns404()
    {
        var ownerId = Guid.NewGuid();
        var ownerFbUid = $"fb-owner-{ownerId:N}";
        var otherId = Guid.NewGuid();
        var otherFbUid = $"fb-other-{otherId:N}";
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = ownerId, Email = $"owner-{ownerId:N}@test.com", FirebaseUid = ownerFbUid });
        db.Users.Add(new User { Id = otherId, Email = $"other-{otherId:N}@test.com", FirebaseUid = otherFbUid });
        var privatePlan = MakePlan("Private Plan", isPublic: false, createdById: ownerId);
        db.Plans.Add(privatePlan);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(otherId, otherFbUid);
        var response = await client.GetAsync($"/plans/{privatePlan.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPlan_PrivatePlan_Owner_ReturnsOk()
    {
        var ownerId = Guid.NewGuid();
        var ownerFbUid = $"fb-privown-{ownerId:N}";
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = ownerId, Email = $"priv-owner-{ownerId:N}@test.com", FirebaseUid = ownerFbUid });
        var privatePlan = MakePlan("Owner Private", isPublic: false, createdById: ownerId);
        db.Plans.Add(privatePlan);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(ownerId, ownerFbUid);
        var response = await client.GetAsync($"/plans/{privatePlan.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPlans_ResponseShape_MatchesPlanDto()
    {
        var db = fixture.GetDbContext();
        db.Plans.Add(MakePlan("Shape Check Showcase", isShowcase: true));
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync("/plans");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var plans = body.GetProperty("plans").EnumerateArray();
        foreach (var plan in plans)
        {
            // Propiedades requeridas por PlanDto (DTO público).
            Assert.True(plan.TryGetProperty("id", out _));
            Assert.True(plan.TryGetProperty("name", out _));
            Assert.True(plan.TryGetProperty("city", out _));
            Assert.True(plan.TryGetProperty("type", out _));
            Assert.True(plan.TryGetProperty("durationDays", out _));
            Assert.True(plan.TryGetProperty("isPublic", out _));
            Assert.True(plan.TryGetProperty("isShowcase", out _));
            Assert.True(plan.TryGetProperty("createdAt", out _));
            // Navegaciones EF no deben aparecer en el JSON.
            Assert.False(plan.TryGetProperty("stops", out _));
            Assert.False(plan.TryGetProperty("createdBy", out _));
            Assert.False(plan.TryGetProperty("followSessions", out _));
        }
    }

    [Fact]
    public async Task GetPlan_ResponseShape_StopsHavePublicPlaceDto()
    {
        var db = fixture.GetDbContext();
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = "Shape Stop Place",
            Category = "Food",
            WhyThisPlace = "Amazing",
            Status = "published",
            // Campos de curación internos que NUNCA deben salir en el DTO público.
            RejectionReason = "internal",
            AiVibeScore = 88,
            Flags = new List<string> { "flag-internal" }
        };
        var plan = MakePlan("Plan Shape With Stops");
        db.Places.Add(place);
        db.Plans.Add(plan);
        db.PlanStops.Add(new PlanStop
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            PlaceId = place.Id,
            DayNumber = 1,
            OrderIndex = 0,
            TimeBlock = "morning"
        });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/plans/{plan.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var day = body.GetProperty("days").EnumerateArray().First();
        var stop = day.GetProperty("stops").EnumerateArray().First();

        Assert.True(stop.TryGetProperty("id", out _));
        Assert.True(stop.TryGetProperty("placeId", out _));
        Assert.True(stop.TryGetProperty("dayNumber", out _));
        Assert.True(stop.TryGetProperty("orderIndex", out _));

        var stopPlace = stop.GetProperty("place");
        Assert.True(stopPlace.TryGetProperty("name", out _));
        Assert.True(stopPlace.TryGetProperty("whyThisPlace", out _));
        // Curación interna NO debe aparecer en el DTO público de Place.
        Assert.False(stopPlace.TryGetProperty("rejectionReason", out _));
        Assert.False(stopPlace.TryGetProperty("aiVibeScore", out _));
        Assert.False(stopPlace.TryGetProperty("submittedById", out _));
        Assert.False(stopPlace.TryGetProperty("reviewedById", out _));
        Assert.False(stopPlace.TryGetProperty("flags", out _));
    }

    // ── POST /plans (builder manual): acepta, valida y persiste StartDate. Paridad total con
    // ── /builder/chat y /chat/generate (misma validacion IsStartDateWithinWindow, mismo formato
    // ── ISO yyyy-MM-dd, misma semantica null=compat, mismo 400 invalid_start_date). El builder
    // ── manual NO corre scheduler: la fecha es solo persistencia/display.

    [Fact]
    public async Task CreatePlan_WithValidStartDate_PersistsAndGetReturnsIt()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-create-sd-{userId:N}";
        var client = await fixture.CreateAuthenticatedClientWithUser(userId, firebaseUid, $"create-sd-{userId:N}@test.com");

        // Fecha dentro de la ventana ([today-1, today+365]). El controller usa DateTime.UtcNow real.
        var startDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        var startIso = startDate.ToString("yyyy-MM-dd");

        var createResponse = await client.PostAsJsonAsync("/plans", new
        {
            name = "Manual Plan With Date",
            city = "Miami",
            durationDays = 2,
            startDate = startIso,
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var createBody = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var planId = createBody.GetProperty("id").GetGuid();
        // La respuesta de creacion ya expone la fecha persistida.
        Assert.Equal(startIso, createBody.GetProperty("startDate").GetString());

        // Round-trip real por Postgres: el GET la relee de la fila persistida.
        var getResponse = await client.GetAsync($"/plans/{planId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(startIso, getBody.GetProperty("startDate").GetString());

        // Y por DbContext directo: Postgres `date` round-trips DateOnly.
        var db = fixture.GetDbContext();
        var persisted = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == planId);
        Assert.Equal(startDate, persisted.StartDate);
    }

    [Fact]
    public async Task CreatePlan_WithoutStartDate_PersistsNull()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-create-nosd-{userId:N}";
        var client = await fixture.CreateAuthenticatedClientWithUser(userId, firebaseUid, $"create-nosd-{userId:N}@test.com");

        var createResponse = await client.PostAsJsonAsync("/plans", new
        {
            name = "Manual Plan No Date",
            city = "Miami",
            durationDays = 1,
            // sin startDate => compat
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var createBody = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var planId = createBody.GetProperty("id").GetGuid();

        var db = fixture.GetDbContext();
        var persisted = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == planId);
        Assert.Null(persisted.StartDate);

        // GET no rompe: startDate ausente (WhenWritingNull) o explicitamente null.
        var getResponse = await client.GetAsync($"/plans/{planId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        if (getBody.TryGetProperty("startDate", out var sd))
            Assert.Equal(JsonValueKind.Null, sd.ValueKind);
    }

    [Fact]
    public async Task CreatePlan_WithOutOfWindowStartDate_Returns400AndDoesNotPersist()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-create-badsd-{userId:N}";
        var client = await fixture.CreateAuthenticatedClientWithUser(userId, firebaseUid, $"create-badsd-{userId:N}@test.com");

        // Fuera de ventana: mas alla de MaxTripHorizonDays (365) en el futuro.
        var badDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(400).ToString("yyyy-MM-dd");
        const string uniqueName = "Manual Plan Out Of Window Date";

        var createResponse = await client.PostAsJsonAsync("/plans", new
        {
            name = uniqueName,
            city = "Miami",
            durationDays = 1,
            startDate = badDate,
        });

        Assert.Equal(HttpStatusCode.BadRequest, createResponse.StatusCode);
        var body = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_start_date", body.GetProperty("error").GetString());

        // No se persistio ninguna fila (fallo antes del SaveChanges).
        var db = fixture.GetDbContext();
        Assert.False(await db.Plans.AsNoTracking().AnyAsync(p => p.Name == uniqueName));
    }

    // ── Bordes exactos de la ventana [today-1, today+MaxTripHorizonDays] via HTTP real. Cierra
    // ── el MINOR de review: los tests de arriba solo pineaban el interior (+30) y muy fuera
    // ── (+400); estos pinean el borde exacto en ambos extremos, aceptado y rechazado. El offset
    // ── se calcula sobre el mismo DateTime.UtcNow que usa el controller (ver PlansController.cs).

    [Theory]
    [InlineData(-1)]  // ayer: borde inferior aceptado (margen de 1 dia por desfase de huso horario)
    [InlineData(365)] // MaxTripHorizonDays: borde superior aceptado
    public async Task CreatePlan_WithStartDateAtAcceptedBoundary_Returns201AndPersists(int offsetDays)
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-create-sdb-{offsetDays}-{userId:N}";
        var client = await fixture.CreateAuthenticatedClientWithUser(userId, firebaseUid, $"create-sdb-{offsetDays}-{userId:N}@test.com");

        var startDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offsetDays);
        var startIso = startDate.ToString("yyyy-MM-dd");
        var uniqueName = $"Manual Plan Boundary Accept {offsetDays} {userId:N}";

        var createResponse = await client.PostAsJsonAsync("/plans", new
        {
            name = uniqueName,
            city = "Miami",
            durationDays = 1,
            startDate = startIso,
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var createBody = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var planId = createBody.GetProperty("id").GetGuid();
        Assert.Equal(startIso, createBody.GetProperty("startDate").GetString());

        var db = fixture.GetDbContext();
        var persisted = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == planId);
        Assert.Equal(startDate, persisted.StartDate);
    }

    [Theory]
    [InlineData(-2)]  // anteayer: justo por debajo del margen de -1 dia
    [InlineData(366)] // justo por encima de MaxTripHorizonDays (365)
    public async Task CreatePlan_WithStartDateJustOutsideBoundary_Returns400AndDoesNotPersist(int offsetDays)
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-create-sdr-{offsetDays}-{userId:N}";
        var client = await fixture.CreateAuthenticatedClientWithUser(userId, firebaseUid, $"create-sdr-{offsetDays}-{userId:N}@test.com");

        var badDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offsetDays).ToString("yyyy-MM-dd");
        var uniqueName = $"Manual Plan Boundary Reject {offsetDays} {userId:N}";

        var createResponse = await client.PostAsJsonAsync("/plans", new
        {
            name = uniqueName,
            city = "Miami",
            durationDays = 1,
            startDate = badDate,
        });

        Assert.Equal(HttpStatusCode.BadRequest, createResponse.StatusCode);
        var body = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_start_date", body.GetProperty("error").GetString());

        var db = fixture.GetDbContext();
        Assert.False(await db.Plans.AsNoTracking().AnyAsync(p => p.Name == uniqueName));
    }

    private static Plan MakePlan(
        string name,
        bool isPublic = true,
        bool isShowcase = false,
        Guid? createdById = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        City = "Miami",
        Type = "curated",
        IsPublic = isPublic,
        IsShowcase = isShowcase,
        CreatedById = createdById
    };
}
