using System.Text.Json;

namespace LocalList.API.Tests.Features;

public class AuthTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Register_NewUser_ReturnsTokens()
    {
        var client = fixture.CreateClient();
        var email = $"reg-new-{Guid.NewGuid():N}@test.com";

        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "Password123!",
            name = "New User"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("accessToken", out _));
        Assert.True(body.TryGetProperty("refreshToken", out _));
        Assert.Equal(email, body.GetProperty("user").GetProperty("email").GetString());
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns400()
    {
        var client = fixture.CreateClient();
        var email = $"reg-dup-{Guid.NewGuid():N}@test.com";

        await client.PostAsJsonAsync("/auth/register", new { email, password = "Password123!" });

        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "Password456!"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsTokens()
    {
        var client = fixture.CreateClient();
        var email = $"login-ok-{Guid.NewGuid():N}@test.com";

        await client.PostAsJsonAsync("/auth/register", new { email, password = "Password123!" });

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            email,
            password = "Password123!"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("accessToken", out _));
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var client = fixture.CreateClient();
        var email = $"login-bad-{Guid.NewGuid():N}@test.com";

        await client.PostAsJsonAsync("/auth/register", new { email, password = "Password123!" });

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            email,
            password = "WrongPassword99!"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_NonExistentUser_Returns401()
    {
        var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            email = $"nobody-{Guid.NewGuid():N}@test.com",
            password = "Password123!"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_ValidToken_ReturnsNewTokens()
    {
        var client = fixture.CreateClient();
        var email = $"refresh-ok-{Guid.NewGuid():N}@test.com";

        var regResponse = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "Password123!"
        });
        var regBody = await regResponse.Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = regBody.GetProperty("refreshToken").GetString()!;

        var response = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("accessToken", out _));
        Assert.True(body.TryGetProperty("refreshToken", out _));
    }

    [Fact]
    public async Task Refresh_InvalidToken_Returns401()
    {
        var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/refresh", new
        {
            refreshToken = "0123456789ABCDEF_this_is_totally_invalid"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
