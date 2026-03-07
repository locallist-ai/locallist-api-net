using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

public class AccountTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task GetAccount_Authenticated_ReturnsUserData()
    {
        var userId = Guid.NewGuid();
        var email = $"acct-get-{userId:N}@test.com";

        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = email, Name = "Test User" });
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(userId, email);
        var response = await client.GetAsync("/account");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AccountEnvelope>();
        Assert.NotNull(body?.User);
        Assert.Equal(email, body.User.Email);
        Assert.Equal("Test User", body.User.Name);
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
        var userId = Guid.NewGuid();
        var client = fixture.CreateAuthenticatedClient(userId);
        var response = await client.GetAsync("/account");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteAccount_RemovesUser()
    {
        var userId = Guid.NewGuid();
        var email = $"acct-del-{userId:N}@test.com";

        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = email });
        db.Plans.Add(new Plan { Name = "Orphan Plan", Type = "ai", City = "Miami", CreatedById = userId });
        await db.SaveChangesAsync();

        var client = fixture.CreateAuthenticatedClient(userId, email);
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
