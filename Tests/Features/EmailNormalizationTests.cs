using System.Text.Json;
using LocalList.API.NET.Features.Auth.Services;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Higiene de email: la normalización (lower+trim en TODA escritura/comparación) + el esquema
/// citext (unicidad e igualdad case-insensitive) impiden cuentas duplicadas por variante de caja
/// o espacios (Pablo@ vs pablo@ vs " pablo@ "). Auth es sensible → cobertura por MUTACIÓN:
///  - quitar la normalización del write → cae DupCaseInsensitive_Register o StoredEmail_IsCanonical.
///  - quitar el ALTER citext         → cae Schema_CaseVariants_ViolateUnique o Sso_LinksLegacyMixedCase.
/// </summary>
public class EmailNormalizationTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const string ValidPassword = "Passw0rd!";

    // ── Test 1: dos registros que solo difieren en la caja → el segundo 409 ────────────
    [Fact]
    public async Task DupCaseInsensitive_Register_SecondReturns409()
    {
        var g = Guid.NewGuid().ToString("N");
        var mixed = $"Pablo{g}@x.com";
        var lower = $"pablo{g}@x.com";

        var client = fixture.CreateClient();

        var first = await client.PostAsJsonAsync("/auth/register",
            new { email = mixed, password = ValidPassword, name = "Pablo" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/auth/register",
            new { email = lower, password = ValidPassword, name = "Pablo" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // Y solo hay una fila para ese email (case-insensitive bajo citext).
        var db = fixture.GetDbContext();
        var rows = await db.Users.CountAsync(u => u.Email == lower);
        Assert.Equal(1, rows);
    }

    // ── Test 2: login con la caja distinta a la del registro → 200 ─────────────────────
    [Fact]
    public async Task Login_DifferentCase_FindsAccount()
    {
        var g = Guid.NewGuid().ToString("N");
        var lower = $"pablo{g}@x.com";
        var upper = $"PABLO{g.ToUpperInvariant()}@X.COM";

        var client = fixture.CreateClient();
        var reg = await client.PostAsJsonAsync("/auth/register",
            new { email = lower, password = ValidPassword, name = "Pablo" });
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);

        var login = await client.PostAsJsonAsync("/auth/login",
            new { email = upper, password = ValidPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(lower, body.GetProperty("user").GetProperty("email").GetString());
    }

    // ── Test 3: registro con espacios → fila almacenada canónica, una sola fila, login OK ─
    [Fact]
    public async Task Register_WithSurroundingSpaces_StoresTrimmedLowercase()
    {
        var g = Guid.NewGuid().ToString("N");
        var canonical = $"pablo{g}@x.com";
        var padded = $"  {canonical}  ";

        var client = fixture.CreateClient();
        var reg = await client.PostAsJsonAsync("/auth/register",
            new { email = padded, password = ValidPassword, name = "Pablo" });
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);

        var body = await reg.Content.ReadFromJsonAsync<JsonElement>();
        var id = Guid.Parse(body.GetProperty("user").GetProperty("id").GetString()!);

        var db = fixture.GetDbContext();
        var dbUser = await db.Users.FindAsync(id);
        Assert.NotNull(dbUser);
        Assert.Equal(canonical, dbUser!.Email); // fila almacenada = lower(trim(entrada))

        var rows = await db.Users.CountAsync(u => u.Email == canonical);
        Assert.Equal(1, rows);

        // Login con la forma canónica (sin espacios) encuentra la cuenta.
        var login = await client.PostAsJsonAsync("/auth/login",
            new { email = canonical, password = ValidPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // ── Test 5: aserción de DB — email almacenado == lower(trim(entrada)) ──────────────
    [Fact]
    public async Task StoredEmail_IsCanonical_LowerTrimmed()
    {
        var g = Guid.NewGuid().ToString("N");
        var input = $"  MixedCase{g.ToUpperInvariant()}@Example.COM ";
        var expected = input.Trim().ToLowerInvariant();

        var client = fixture.CreateClient();
        var reg = await client.PostAsJsonAsync("/auth/register",
            new { email = input, password = ValidPassword, name = "Case" });
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);

        var body = await reg.Content.ReadFromJsonAsync<JsonElement>();
        var id = Guid.Parse(body.GetProperty("user").GetProperty("id").GetString()!);

        var db = fixture.GetDbContext();
        var dbUser = await db.Users.FindAsync(id);
        Assert.NotNull(dbUser);
        Assert.Equal(expected, dbUser!.Email);
    }

    // ── Test 4: SSO (Apple) con email en mayúsculas enlaza a la cuenta legada ──────────
    //   La fila legada tiene el email en caja MIXTA (dato sucio pre-normalización). El link
    //   por email exige la igualdad case-insensitive de citext → sin citext crearía duplicado.
    [Fact]
    public async Task Sso_MixedCaseEmail_LinksLegacyUser_NoDuplicate()
    {
        var g = Guid.NewGuid().ToString("N");
        var legacyStored = $"Sso{g}@x.com";       // caja mixta almacenada (legado)
        var claimEmail = $"SSO{g.ToUpperInvariant()}@X.COM"; // el provider lo devuelve en mayúsculas
        var userId = Guid.NewGuid();
        var appleSub = $"apple-{g}";
        var idToken = $"apple-token-{g}";

        // Fila legada: solo firebase_uid, SIN apple_user_id, email en caja mixta.
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = legacyStored, FirebaseUid = $"fb-{g}" });
        await db.SaveChangesAsync();

        fixture.FakeApple.Tokens[idToken] =
            new OAuthClaims(appleSub, claimEmail, "Legacy User", null);

        var client = fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/auth/signin",
            new { provider = "apple", idToken });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(userId.ToString(), body.GetProperty("user").GetProperty("id").GetString());

        // No se creó duplicado y el apple_user_id quedó enlazado a la MISMA fila.
        var db2 = fixture.GetDbContext();
        var rows = await db2.Users.CountAsync(u => u.Email == claimEmail);
        Assert.Equal(1, rows);
        var linked = await db2.Users.FindAsync(userId);
        Assert.Equal(appleSub, linked!.AppleUserId);
    }

    // ── Test 6: nivel esquema — dos variantes de caja insertadas directas → unique violation ─
    [Fact]
    public async Task Schema_CaseVariants_ViolateUniqueIndex()
    {
        var g = Guid.NewGuid().ToString("N");
        var lower = $"schema{g}@x.com";
        var upper = $"SCHEMA{g.ToUpperInvariant()}@X.COM";

        var db = fixture.GetDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO users (id, email, tier, role, created_at, updated_at) " +
            "VALUES (gen_random_uuid(), {0}, 'free', 'user', now(), now())", lower);

        // La segunda variante SOLO difiere en la caja → el índice único citext la rechaza (23505).
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlRawAsync(
                "INSERT INTO users (id, email, tier, role, created_at, updated_at) " +
                "VALUES (gen_random_uuid(), {0}, 'free', 'user', now(), now())", upper));

        var pg = ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException;
        Assert.NotNull(pg);
        Assert.Equal("23505", pg!.SqlState);
    }
}
