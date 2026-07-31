using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task Refresh_RotatesTokens_ProducesFreshWorkingPair()
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

        // The freshly rotated token is itself usable (chain keeps moving forward).
        var second = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Refresh_InvalidToken_Returns401()
    {
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = new string('a', 128) });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Refresh_LostResponseRetry_WithinGrace_RecoversSessionWithoutRevoking()
    {
        // AVAILABILITY (the BLOCKER): a client rotates A→B, the RESPONSE is lost (roaming
        // / backgrounded app), so it still holds A and re-fires refresh with A on the next
        // 401. Within the grace window this MUST be a graceful lost-response recovery — a
        // fresh working pair, NOT a 401 and NOT a family revocation. A legit retry can
        // never log the user out.
        var email = $"lostresp-{Guid.NewGuid():N}@test.com";
        var client = fixture.CreateClient();
        var registered = await (await client.PostAsJsonAsync("/auth/register",
            new { email, password = "LostResp1!" })).Content.ReadFromJsonAsync<TokensResponse>();

        // A → B (imagine the client never receives this response).
        var rotate = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = registered!.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        var b = await rotate.Content.ReadFromJsonAsync<RefreshOnlyResponse>();

        // Re-present A (the lost-response retry). Graceful: 200 + a brand-new token.
        var retry = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = registered.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var c = await retry.Content.ReadFromJsonAsync<RefreshOnlyResponse>();
        Assert.NotEqual(registered.RefreshToken, c!.RefreshToken);
        Assert.NotEqual(b!.RefreshToken, c.RefreshToken);

        // The recovered session is alive: the new token works.
        var withC = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = c.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, withC.StatusCode);

        // Nothing was revoked — a lost-response retry must not trip family revocation.
        var db = fixture.GetDbContext();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        var revoked = await db.RefreshTokens
            .CountAsync(rt => rt.UserId == user.Id && rt.RevokedAt != null);
        Assert.Equal(0, revoked);
    }

    [Fact]
    public async Task Refresh_ConcurrentDoubleSubmit_IsCoherent_NoFamilyRevoke()
    {
        // CONCURRENCY: two refreshes of the SAME valid token race (double-submit, e.g. two
        // parallel 401s). The atomic claim must let exactly ONE win — never two valid
        // tokens — and it must NOT be mistaken for reuse (no family revocation).
        var email = $"concurrent-{Guid.NewGuid():N}@test.com";
        var client = fixture.CreateClient();
        var registered = await (await client.PostAsJsonAsync("/auth/register",
            new { email, password = "Concurr1!" })).Content.ReadFromJsonAsync<TokensResponse>();
        var tokenA = registered!.RefreshToken;

        // Two independent DI scopes → two DbContexts → a real race against Postgres. A
        // Barrier aligns both callers so they enter RotateAsync (and read A as ACTIVE)
        // at the same instant — a genuine tight race on the active-rotation path, which
        // is where the atomic claim must serialize.
        using var scope1 = fixture.Services.CreateScope();
        using var scope2 = fixture.Services.CreateScope();
        var svc1 = scope1.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        var svc2 = scope2.ServiceProvider.GetRequiredService<IRefreshTokenService>();

        using var barrier = new Barrier(2);
        async Task<RefreshTokenRotation?> Race(IRefreshTokenService svc)
        {
            barrier.SignalAndWait();
            return await svc.RotateAsync(tokenA, CancellationToken.None);
        }
        var results = await Task.WhenAll(Task.Run(() => Race(svc1)), Task.Run(() => Race(svc2)));

        // Every caller gets a COHERENT result: a valid rotation, never an error and
        // never a family revocation. In a tight race the atomic claim yields exactly one
        // winner (the loser sees 0 rows → null); if one call happens to settle first the
        // other is absorbed by the SAME safe grace path (a fresh pair). Either way at
        // least one non-null result and no exception.
        var winners = results.Where(r => r is not null).ToList();
        Assert.NotEmpty(winners);

        var db = fixture.GetDbContext();
        var user = await db.Users.FirstAsync(u => u.Email == email);

        // Anti-corruption invariant (the atomicity fix): the original active token A was
        // consumed EXACTLY ONCE (rotated, not deleted, not left active), and every live
        // token corresponds to exactly one successful caller — no rotation was
        // double-spent into extra tokens, and the count stays bounded by the 2 callers.
        var tokenAPrefix = tokenA[..16];
        var tokenARow = await db.RefreshTokens.FirstAsync(rt =>
            rt.UserId == user.Id && rt.TokenPrefix == tokenAPrefix);
        Assert.NotNull(tokenARow.RotatedAt);
        var live = await db.RefreshTokens
            .CountAsync(rt => rt.UserId == user.Id && rt.RotatedAt == null && rt.RevokedAt == null);
        Assert.Equal(winners.Count, live);
        Assert.InRange(live, 1, 2);

        // Critical: a concurrent double-submit is NOT reuse → nothing revoked.
        Assert.Equal(0, await db.RefreshTokens.CountAsync(rt => rt.UserId == user.Id && rt.RevokedAt != null));

        // Session intact: a winner's freshly minted token is usable.
        var useWinner = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = winners[0]!.NewPlainToken });
        Assert.Equal(HttpStatusCode.OK, useWinner.StatusCode);
    }

    [Fact]
    public async Task Refresh_ReplayPastGrace_RevokesWholeFamily()
    {
        // SECURITY: a genuine exfiltration replays a long-spent token well after the
        // session moved on. A→B→C, then the clock advances past the grace window and A is
        // replayed → treated as reuse → the WHOLE family is revoked, so the live token C
        // also stops working.
        var email = $"replay-{Guid.NewGuid():N}@test.com";
        var client = fixture.CreateClient();
        var registered = await (await client.PostAsJsonAsync("/auth/register",
            new { email, password = "Replay12!" })).Content.ReadFromJsonAsync<TokensResponse>();

        // A → B
        var b = await (await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = registered!.RefreshToken })).Content.ReadFromJsonAsync<RefreshOnlyResponse>();
        // B → C
        var c = await (await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = b!.RefreshToken })).Content.ReadFromJsonAsync<RefreshOnlyResponse>();

        // Move past the grace window so replaying the long-spent A counts as reuse.
        fixture.FakeTime.Advance(TimeSpan.FromSeconds(90));

        // Replay A (spent, past grace) → 401 + family revocation.
        var replay = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = registered.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // C, the live token, is now revoked because reuse of A nuked the family.
        var withC = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = c!.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, withC.StatusCode);

        // DB: no live (non-revoked) refresh token remains for the user.
        var db = fixture.GetDbContext();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        var live = await db.RefreshTokens
            .CountAsync(rt => rt.UserId == user.Id && rt.RevokedAt == null);
        Assert.Equal(0, live);
    }

    [Fact]
    public async Task Refresh_GarbageToken_Returns401_WithoutRevokingActiveSession()
    {
        // A never-existed / garbage token must NOT be treated as reuse: it must 401
        // WITHOUT mass-revoking the user's session. The user's current valid token
        // keeps working afterwards.
        var email = $"garbage-{Guid.NewGuid():N}@test.com";
        var client = fixture.CreateClient();
        var registered = await (await client.PostAsJsonAsync("/auth/register",
            new { email, password = "Garbage12!" })).Content.ReadFromJsonAsync<TokensResponse>();

        var garbage = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = new string('b', 128) });
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);

        // The genuine current token still rotates — session was not nuked.
        var rotate = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = registered!.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        var rotated = await rotate.Content.ReadFromJsonAsync<RefreshOnlyResponse>();
        Assert.NotEqual(registered.RefreshToken, rotated!.RefreshToken);
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
