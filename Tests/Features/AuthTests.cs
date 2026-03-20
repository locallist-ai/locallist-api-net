using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

public class AuthTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Sync_NewUser_CreatesUser()
    {
        var firebaseUid = $"fb-new-{Guid.NewGuid():N}";
        var email = $"sync-new-{Guid.NewGuid():N}@test.com";

        var client = fixture.CreateClient();
        var token = fixture.CreateToken(firebaseUid, email);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/auth/sync", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var user = body.GetProperty("user");
        Assert.Equal(email, user.GetProperty("email").GetString());
        Assert.Equal("user", user.GetProperty("role").GetString());

        // Verify user was created in DB
        var db = fixture.GetDbContext();
        var dbUser = await db.Users.FindAsync(Guid.Parse(user.GetProperty("id").GetString()!));
        Assert.NotNull(dbUser);
        Assert.Equal(firebaseUid, dbUser.FirebaseUid);
    }

    [Fact]
    public async Task Sync_ExistingUserByEmail_LinksFirebaseUid()
    {
        var firebaseUid = $"fb-link-{Guid.NewGuid():N}";
        var email = $"sync-link-{Guid.NewGuid():N}@test.com";
        var userId = Guid.NewGuid();

        // Pre-create user without firebase_uid (simulating migration)
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = email, Name = "Existing User" });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var token = fixture.CreateToken(firebaseUid, email);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/auth/sync", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(userId.ToString(), body.GetProperty("user").GetProperty("id").GetString());

        // Verify firebase_uid was linked
        var db2 = fixture.GetDbContext();
        var dbUser = await db2.Users.FindAsync(userId);
        Assert.Equal(firebaseUid, dbUser!.FirebaseUid);
    }

    [Fact]
    public async Task Sync_ExistingFirebaseUser_ReturnsUser()
    {
        var firebaseUid = $"fb-exist-{Guid.NewGuid():N}";
        var email = $"sync-exist-{Guid.NewGuid():N}@test.com";
        var userId = Guid.NewGuid();

        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = email, FirebaseUid = firebaseUid });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var token = fixture.CreateToken(firebaseUid, email);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/auth/sync", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(userId.ToString(), body.GetProperty("user").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Sync_AdminEmail_GetsAdminRole()
    {
        var firebaseUid = $"fb-admin-{Guid.NewGuid():N}";
        var email = $"admin-{Guid.NewGuid():N}@locallist.ai";

        var client = fixture.CreateClient();
        var token = fixture.CreateToken(firebaseUid, email);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/auth/sync", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", body.GetProperty("user").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Sync_Unauthenticated_Returns401()
    {
        var client = fixture.CreateClient();
        var response = await client.PostAsync("/auth/sync", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
