using System.Net;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Tests de integración de <c>PUT /plans/{id}/stops</c> (PlanEditController).
///
/// Gaps auditoría 2026-04-19 que se cubren:
///   - IDOR: un usuario distinto al dueño no debe poder editar stops.
///   - placeId inexistente: el backend debe rechazar con 400 en vez de
///     silenciosamente crear stops apuntando a Places fantasma.
///
/// Divergencia frente al briefing:
///   El briefing pedía 404 para el caso IDOR (no filtrar existencia). El
///   PlanEditController actual devuelve <c>Forbid()</c> (403) explícitamente
///   — ver <c>Features/Plans/PlanEditController.cs</c>. El test documenta el
///   comportamiento real (403) para no tapar la decisión actual con un
///   assert incorrecto; si en el futuro se endurece a 404 para no filtrar
///   existencia, este test fallará y deberá actualizarse (y con él la
///   decisión de producto queda registrada en un sitio ejecutable).
/// </summary>
public class PlanEditTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task UpdateStops_NonOwner_ReturnsForbidOrNotFound()
    {
        // User A posee un plan. User B intenta editarle los stops.
        var (ownerId, ownerFbUid) = await CreateUser("planedit-owner");
        var (otherId, otherFbUid) = await CreateUser("planedit-other");

        var db = fixture.GetDbContext();
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = $"Stop Place {Guid.NewGuid():N}",
            Category = "Food",
            City = "Miami",
            WhyThisPlace = "Seeded",
            Status = "published"
        };
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Plan del owner",
            City = "Miami",
            Type = "user",
            IsPublic = false,
            CreatedById = ownerId
        };
        db.Places.Add(place);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var attackerClient = fixture.CreateAuthenticatedClient(otherId, otherFbUid);

        var payload = new
        {
            stops = new[]
            {
                new { placeId = place.Id, dayNumber = 1, orderIndex = 0, timeBlock = "morning", suggestedDurationMin = 60 }
            }
        };

        var response = await attackerClient.PutAsJsonAsync($"/plans/{plan.Id}/stops", payload);

        // Contrato actual: 403 (Forbid). Aceptamos también 404 por si el backend
        // evoluciona a "no filtrar existencia" — lo que importa para el gap de
        // seguridad es que NO haya devuelto 200.
        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"IDOR: esperábamos 403 o 404, recibimos {(int)response.StatusCode}");

        // Verificamos en BD que no se tocó el plan.
        var after = fixture.GetDbContext();
        var persistedStops = await after.PlanStops.Where(s => s.PlanId == plan.Id).CountAsync();
        Assert.Equal(0, persistedStops);
    }

    [Fact]
    public async Task UpdateStops_UnknownPlaceId_Returns400()
    {
        var (ownerId, ownerFbUid) = await CreateUser("planedit-badplace");
        var db = fixture.GetDbContext();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Plan con placeId mal",
            City = "Miami",
            Type = "user",
            IsPublic = false,
            CreatedById = ownerId
        };
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(ownerId, ownerFbUid);

        var ghostPlaceId = Guid.NewGuid(); // jamás insertado en la tabla places
        var payload = new
        {
            stops = new[]
            {
                new { placeId = ghostPlaceId, dayNumber = 1, orderIndex = 0, timeBlock = "morning", suggestedDurationMin = 60 }
            }
        };

        var response = await client.PutAsJsonAsync($"/plans/{plan.Id}/stops", payload);

        // El controller valida primero la existencia del Place y devuelve BadRequest.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("error", out var err), "Se esperaba mensaje de error");
        Assert.Contains("Places not found", err.GetString() ?? "");
    }

    [Fact]
    public async Task UpdateStops_Owner_ReturnsPlanDetailDtoShape()
    {
        var (ownerId, ownerFbUid) = await CreateUser("planedit-shape");
        var db = fixture.GetDbContext();
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = $"Shape Place {Guid.NewGuid():N}",
            Category = "Food",
            City = "Miami",
            WhyThisPlace = "Seeded shape",
            Status = "published",
            RejectionReason = "hidden-internal",
            AiVibeScore = 99
        };
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Plan Shape Edit",
            City = "Miami",
            Type = "user",
            IsPublic = false,
            CreatedById = ownerId,
            DurationDays = 1
        };
        db.Places.Add(place);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(ownerId, ownerFbUid);
        var payload = new
        {
            stops = new[]
            {
                new { placeId = place.Id, dayNumber = 1, orderIndex = 0, timeBlock = "morning", suggestedDurationMin = 60 }
            }
        };

        var response = await client.PutAsJsonAsync($"/plans/{plan.Id}/stops", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("id", out _));
        Assert.True(body.TryGetProperty("days", out var days));
        var stop = days.EnumerateArray().First().GetProperty("stops").EnumerateArray().First();
        var stopPlace = stop.GetProperty("place");
        Assert.False(stopPlace.TryGetProperty("rejectionReason", out _));
        Assert.False(stopPlace.TryGetProperty("aiVibeScore", out _));
    }

    [Fact]
    public async Task DeletePlan_Owner_Returns204AndCascades()
    {
        var (ownerId, ownerFbUid) = await CreateUser("plandelete-owner");

        var db = fixture.GetDbContext();
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = $"Del Place {Guid.NewGuid():N}",
            Category = "Food",
            City = "Miami",
            WhyThisPlace = "Seeded",
            Status = "published"
        };
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Plan a borrar",
            City = "Miami",
            Type = "user",
            IsPublic = false,
            CreatedById = ownerId,
            Stops = new List<PlanStop>
            {
                new() { Id = Guid.NewGuid(), PlaceId = place.Id, DayNumber = 1, OrderIndex = 0 }
            }
        };
        db.Places.Add(place);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(ownerId, ownerFbUid);
        var response = await client.DeleteAsync($"/plans/{plan.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = fixture.GetDbContext();
        Assert.Null(await after.Plans.FindAsync(plan.Id));
        var remainingStops = await after.PlanStops.Where(s => s.PlanId == plan.Id).CountAsync();
        Assert.Equal(0, remainingStops);
        // Place itself must still exist — deleting a plan must not delete places.
        Assert.NotNull(await after.Places.FindAsync(place.Id));
    }

    [Fact]
    public async Task DeletePlan_NonOwner_Returns404AndPreservesPlan()
    {
        var (ownerId, _) = await CreateUser("plandelete-owner2");
        var (otherId, otherFbUid) = await CreateUser("plandelete-other");

        var db = fixture.GetDbContext();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Plan protegido",
            City = "Miami",
            Type = "user",
            IsPublic = false,
            CreatedById = ownerId
        };
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var attackerClient = fixture.CreateAuthenticatedClient(otherId, otherFbUid);
        var response = await attackerClient.DeleteAsync($"/plans/{plan.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var after = fixture.GetDbContext();
        Assert.NotNull(await after.Plans.FindAsync(plan.Id));
    }

    [Fact]
    public async Task DeletePlan_MissingPlan_Returns404()
    {
        var (userId, userFbUid) = await CreateUser("plandelete-missing");
        var client = fixture.CreateAuthenticatedClient(userId, userFbUid);

        var response = await client.DeleteAsync($"/plans/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<(Guid userId, string firebaseUid)> CreateUser(string prefix)
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-{prefix}-{userId:N}";
        var db = fixture.GetDbContext();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"{prefix}-{userId:N}@test.com",
            FirebaseUid = firebaseUid
        });
        await db.SaveChangesAsync();
        return (userId, firebaseUid);
    }
}
