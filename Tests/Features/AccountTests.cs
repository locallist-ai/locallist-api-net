using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Usage;

namespace LocalList.API.Tests.Features;

public class AccountTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task GetAccount_FreeUser_ExposesAiPlansMonthQuota()
    {
        // m4/F7: /account expone la cuota de generación IA del mes (used/limit/resetsAt) para
        // que la app pinte "X de 3 planes este mes" sin provocar el 403.
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-quota-{userId:N}";
        var email = $"acct-quota-{userId:N}@test.com";
        var client = await fixture.CreateAuthenticatedClientWithUser(userId, firebaseUid, email); // free

        var now = fixture.FakeTime.GetUtcNow();
        var monthStart = new DateOnly(now.Year, now.Month, 1);
        var db = fixture.GetDbContext();
        db.UsageCounters.Add(new UsageCounter
        {
            UserId = userId,
            Feature = PlanGenerationGateService.FeatureMonthly,
            PeriodStart = monthStart,
            Count = 2,
        });
        await db.SaveChangesAsync();

        var response = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var quota = body.GetProperty("aiPlansMonth");
        Assert.Equal(2, quota.GetProperty("used").GetInt32());
        Assert.Equal(PlanGenerationGateService.FreeMonthlyPlanLimit, quota.GetProperty("limit").GetInt32());

        var expectedReset = new DateTimeOffset(
            monthStart.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        Assert.Equal(expectedReset, quota.GetProperty("resetsAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task GetAccount_ProUser_AiPlansMonthLimitIsNull()
    {
        // Plus: el límite mensual no aplica (usan el cap diario antiabuso) → limit:null.
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-quota-pro-{userId:N}";
        var client = await fixture.CreateAuthenticatedClientWithUser(
            userId, firebaseUid, $"acct-quotapro-{userId:N}@test.com", tier: "pro");

        var response = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var quota = body.GetProperty("aiPlansMonth");
        // Plus: limit omitido (WhenWritingNull) → la app lo interpreta como ilimitado.
        Assert.False(quota.TryGetProperty("limit", out _));
        Assert.Equal(0, quota.GetProperty("used").GetInt32());
    }

    [Fact]
    public async Task GetAccount_Authenticated_ReturnsUserData()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-acct-{userId:N}";
        var email = $"acct-get-{userId:N}@test.com";

        var client = await fixture.CreateAuthenticatedClientWithUser(userId, firebaseUid, email);
        var response = await client.GetAsync("/account");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AccountEnvelope>();
        Assert.NotNull(body?.User);
        Assert.Equal(email, body.User.Email);
    }

    [Fact]
    public async Task GetAccount_Unauthenticated_Returns401()
    {
        var client = fixture.CreateClient();
        var response = await client.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAccount_UserNotInDb_Returns404()
    {
        var firebaseUid = $"fb-missing-{Guid.NewGuid():N}";
        var client = fixture.CreateAuthenticatedClient(Guid.NewGuid(), firebaseUid);
        var response = await client.GetAsync("/account");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteAccount_RemovesUser()
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-del-{userId:N}";
        var email = $"acct-del-{userId:N}@test.com";

        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = email, FirebaseUid = firebaseUid });
        db.Plans.Add(new Plan { Name = "Orphan Plan", Type = "ai", City = "Miami", CreatedById = userId });
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(userId, firebaseUid, email);
        var response = await client.DeleteAsync("/account");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var db2 = fixture.GetDbContext();
        Assert.Null(await db2.Users.FindAsync(userId));

        var plan = await db2.Plans.FirstOrDefaultAsync(p => p.Name == "Orphan Plan");
        Assert.NotNull(plan);
        Assert.Null(plan.CreatedById);
    }

    private record UserDto(Guid Id, string Email, string? Name, string? Image, string Tier, string? City);
    private record AccountEnvelope(UserDto User);
}
