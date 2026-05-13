using System.Net.Http.Headers;
using LocalList.API.NET.Features.Auth.Services;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

public class AppAuthTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private record TokensResponse(string AccessToken, string RefreshToken, AuthUserDto User);
    private record AuthUserDto(Guid Id, string Email, string? Name, string? Image, string Tier);
    private record RefreshOnlyResponse(string AccessToken, string RefreshToken);

    // ─── Register ────────────────────────────────────────

    [Fact]
    public async Task Register_HappyPath_ReturnsTokensAndPersistsUser()
    {
        var email = $"reg-{Guid.NewGuid():N}@test.com";
        var client = fixture.CreateClient();

        var res = await client.PostAsJsonAsync("/auth/register",
            new { email, password = "StrongPass1!", name = "Pablo" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<TokensResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body!.AccessToken));
        Assert.False(string.IsNullOrEmpty(body.RefreshToken));
        Assert.Equal(email, body.User.Email);

        var db = fixture.GetDbContext();
        var saved = await db.Users.FirstAsync(u => u.Email == email);
        Assert.False(string.IsNullOrEmpty(saved.PasswordHash));
        Assert.True(await db.RefreshTokens.AnyAsync(rt => rt.UserId == saved.Id));
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        var email = $"dup-{Guid.NewGuid():N}@test.com";
        var client = fixture.CreateClient();

        var first = await client.PostAsJsonAsync("/auth/register",
            new { email, password = "Password1!" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/auth/register",
            new { email, password = "AnotherPass2!" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Register_PasswordTooShort_Returns400()
    {
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/register",
            new { email = $"short-{Guid.NewGuid():N}@test.com", password = "abc" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Register_WithWeakPassword_Returns400()
    {
        // "password" satisface MinimumLength=8 pero no la regex (sin mayúscula/dígito/especial).
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/register",
            new { email = $"weak-{Guid.NewGuid():N}@test.com", password = "password", name = "Test" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("uppercase", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_WithStrongPassword_Returns200()
    {
        var email = $"strong-{Guid.NewGuid():N}@test.com";
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/register",
            new { email, password = "StrongP4ss!", name = "Test" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Register_AdminDomainEmail_Returns201()
    {
        // @locallist.ai can register in the user app (co-founders, internal testers).
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/register",
            new { email = $"internal-{Guid.NewGuid():N}@locallist.ai", password = "Test1234!" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ─── Login ───────────────────────────────────────────

    [Fact]
    public async Task Login_CorrectCredentials_ReturnsTokens()
    {
        var email = $"login-{Guid.NewGuid():N}@test.com";
        var password = "MySecret1!";
        var client = fixture.CreateClient();

        await client.PostAsJsonAsync("/auth/register", new { email, password });

        var res = await client.PostAsJsonAsync("/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<TokensResponse>();
        Assert.False(string.IsNullOrEmpty(body!.AccessToken));
    }

    [Fact]
    public async Task Login_UnknownEmail_Returns401()
    {
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/login",
            new { email = $"ghost-{Guid.NewGuid():N}@test.com", password = "doesnotmatter" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Login_WithInvalidEmail_ReturnsGenericError()
    {
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/login",
            new { email = $"nobody-{Guid.NewGuid():N}@test.com", password = "Whatever1!" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal("{\"error\":\"Invalid credentials\"}", body);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var email = $"wp-{Guid.NewGuid():N}@test.com";
        var client = fixture.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new { email, password = "Correct1!" });

        var res = await client.PostAsJsonAsync("/auth/login", new { email, password = "Wrong1!" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Login_OAuthOnlyAccount_ReturnsSameGenericError()
    {
        // Seedea un usuario OAuth-only (email existe, pero PasswordHash NULL).
        // La respuesta debe ser byte-idéntica a un login con email inexistente
        // → cero enumeración de usuarios.
        var email = $"oauth-{Guid.NewGuid():N}@test.com";
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Email = email, GoogleUserId = "google-oauth-only" });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/login",
            new { email, password = "Whatever1!" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal("{\"error\":\"Invalid credentials\"}", body);
    }

    // ─── Signin (Apple / Google) ─────────────────────────

    [Fact]
    public async Task Signin_Apple_NewUser_CreatesAndReturnsTokens()
    {
        var idToken = $"apple-token-{Guid.NewGuid():N}";
        var email = $"apple-{Guid.NewGuid():N}@privaterelay.appleid.com";
        fixture.FakeApple.Tokens[idToken] = new OAuthClaims(
            Sub: $"apple-sub-{Guid.NewGuid():N}", Email: email, Name: null, Picture: null);

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/signin",
            new { provider = "apple", idToken, name = "Pablo Apple" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<TokensResponse>();
        Assert.Equal(email, body!.User.Email);

        var db = fixture.GetDbContext();
        var saved = await db.Users.FirstAsync(u => u.Email == email);
        Assert.NotNull(saved.AppleUserId);
        Assert.Equal("Pablo Apple", saved.Name);
    }

    [Fact]
    public async Task Signin_Google_LinksProviderToExistingEmail()
    {
        var email = $"link-{Guid.NewGuid():N}@test.com";
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Email = email });
        await db.SaveChangesAsync();

        var idToken = $"google-token-{Guid.NewGuid():N}";
        var sub = $"google-sub-{Guid.NewGuid():N}";
        fixture.FakeGoogle.Tokens[idToken] = new OAuthClaims(
            Sub: sub, Email: email, Name: "Pablo G", Picture: "https://google.com/pic.jpg");

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/signin",
            new { provider = "google", idToken });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var verify = fixture.GetDbContext();
        var linked = await verify.Users.FirstAsync(u => u.Email == email);
        Assert.Equal(sub, linked.GoogleUserId);
    }

    [Fact]
    public async Task Signin_InvalidIdToken_Returns401()
    {
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/signin",
            new { provider = "apple", idToken = "no-such-token-in-fake" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Signin_AdminDomainOAuthEmail_Returns200()
    {
        // @locallist.ai accounts can sign in to the user app (e.g. co-founders testing).
        // Admin access is gated by Firebase + /auth/sync, not by blocking OAuth in the app.
        var idToken = $"google-admin-{Guid.NewGuid():N}";
        fixture.FakeGoogle.Tokens[idToken] = new OAuthClaims(
            Sub: $"google-sub-{Guid.NewGuid():N}",
            Email: $"curator-{Guid.NewGuid():N}@locallist.ai",
            Name: null,
            Picture: null);

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/signin",
            new { provider = "google", idToken });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Signin_UnverifiedEmail_Returns401()
    {
        var idToken = $"google-unverified-{Guid.NewGuid():N}";
        fixture.FakeGoogle.Tokens[idToken] = new OAuthClaims(
            Sub: $"google-sub-{Guid.NewGuid():N}",
            Email: $"unverified-{Guid.NewGuid():N}@test.com",
            Name: null,
            Picture: null)
        {
            EmailVerified = false
        };

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/signin",
            new { provider = "google", idToken });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("not verified", body);
    }

    // ─── Refresh ─────────────────────────────────────────

    [Fact]
    public async Task Refresh_RotatesTokens_AndOldTokenIsInvalidatedAfterUse()
    {
        var email = $"refresh-{Guid.NewGuid():N}@test.com";
        var client = fixture.CreateClient();
        var registered = await (await client.PostAsJsonAsync("/auth/register",
            new { email, password = "Rotate1!" })).Content.ReadFromJsonAsync<TokensResponse>();

        var first = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = registered!.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = await first.Content.ReadFromJsonAsync<RefreshOnlyResponse>();
        Assert.NotEqual(registered.RefreshToken, rotated!.RefreshToken);

        // Reusing the original token must fail (single-use)
        var reuse = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = registered.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task Refresh_InvalidToken_Returns401()
    {
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = new string('a', 128) });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ─── End-to-end multi-scheme: app token reaches authenticated endpoints ───

    [Fact]
    public async Task AppHs256Token_AuthenticatesAgainstAccountEndpoint()
    {
        var email = $"e2e-{Guid.NewGuid():N}@test.com";
        var client = fixture.CreateClient();
        var registered = await (await client.PostAsJsonAsync("/auth/register",
            new { email, password = "EndToEnd1!" })).Content.ReadFromJsonAsync<TokensResponse>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registered!.AccessToken);
        var account = await client.GetAsync("/account");

        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
    }

    [Fact]
    public async Task AppHs256Token_CannotAccessAdminEndpoints()
    {
        // Defense in depth: HS256 app tokens are rejected by admin endpoints even
        // when the email matches @locallist.ai, because AdminAuthorizationFilter
        // requires a Firebase RS256 issuer (https://securetoken.google.com/...).
        var email = $"e2e-admin-{Guid.NewGuid():N}@test.com";
        var client = fixture.CreateClient();
        var registered = await (await client.PostAsJsonAsync("/auth/register",
            new { email, password = "EndToEnd1!" })).Content.ReadFromJsonAsync<TokensResponse>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registered!.AccessToken);

        var res = await client.GetAsync("/admin/plans");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
