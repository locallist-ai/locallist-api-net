using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using System.Collections.Concurrent;
using LocalList.API.NET.Features.Admin.Places;
using LocalList.API.NET.Features.Auth.Services;
using LocalList.API.NET.Features.Builder;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Features.Chat.Services;
using LocalList.API.NET.Features.Import;
using LocalList.API.NET.Shared.AI;
using LocalList.API.NET.Shared.AI.Services;
using LocalList.API.NET.Features.Routing;
using LocalList.API.NET.Shared.Routing;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.PostHog;

namespace LocalList.API.Tests;

public class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestFirebaseProjectId = "test-firebase-project";
    public const string TestJwtSecret = "test-jwt-secret-must-be-at-least-32-bytes-long-aaaaaaaaaaaa";
    private const string TestAppleBundleId = "test.bundle.id";
    private const string TestGoogleClientId = "test-google-client-id.apps.googleusercontent.com";
    private static readonly RSA _testRsa = RSA.Create(2048);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .Build();

    public FakeTimeProvider FakeTime { get; } = new(DateTimeOffset.UtcNow);
    public FakeAppleIdTokenValidator FakeApple { get; } = new();
    public FakeGoogleIdTokenValidator FakeGoogle { get; } = new();

    /// <summary>
    /// Handler compartido que intercepta las llamadas salientes de los servicios Gemini
    /// (PreferenceExtractorService, PlaceTranslatorService, DescriptionGeneratorService).
    /// Los tests pueden sustituir <see cref="FakeGeminiHandler.Responder"/> para definir la respuesta
    /// (OK con JSON válido, 502, texto malformado, etc.) sin levantar un servidor HTTP real.
    /// </summary>
    public FakeGeminiHandler FakeGemini { get; } = new();
    public FakePostHogHandler FakePostHog { get; } = new();

    /// <summary>
    /// In-process fake for <see cref="IGooglePlacesService"/>.
    /// Tests configure <see cref="FakeGooglePlacesService.ResolveResponder"/> and
    /// <see cref="FakeGooglePlacesService.DetailsResponder"/> to control resolution outcomes.
    /// By default all methods return null (simulates missing API key).
    /// </summary>
    public FakeGooglePlacesService FakeGooglePlaces { get; } = new();

    /// <summary>
    /// Handler que intercepta las llamadas salientes de <see cref="MapboxRoutingService"/>.
    /// Tests del routing configuran <see cref="FakeMapboxHandler.Responder"/> para definir
    /// la respuesta (OK con polyline, 502, vacío, etc.). Por defecto devuelve una respuesta
    /// Mapbox mínima válida con una polyline de prueba.
    /// </summary>
    public FakeMapboxHandler FakeMapbox { get; } = new();

    /// <summary>
    /// Handler que intercepta llamadas de <see cref="EmbeddingService"/>
    /// a <c>:batchEmbedContents</c>. Por defecto devuelve embeddings
    /// deterministas (hash-based) de 768 dims, lo que permite a los tests
    /// verificar flujo end-to-end sin tocar la API real de Gemini.
    /// </summary>
    public FakeEmbeddingHandler FakeEmbeddings { get; } = new();

    /// <summary>
    /// Handler que intercepta los providers no-Gemini de la cadena LLM (named clients
    /// llm-openai / llm-mistral / llm-anthropic). Solo entra en juego en tests que
    /// activan esos providers vía UseSetting("OpenAI:ApiKey", ...). Por defecto 503.
    /// </summary>
    public FakeOpenAiHandler FakeOpenAi { get; } = new();

    /// <summary>
    /// Handler que simula la Gemini File API + generateContent multimodal del import de vídeo
    /// (F2). Cablea tanto el <c>GeminiFileClient</c> real (subida resumable / poll / delete) como
    /// el HttpClient de <c>VideoExtractionService</c> (generateContent), así que los tests
    /// ejercitan el código real de retención sin tocar Gemini. Ver <see cref="FakeGeminiFileApi"/>.
    /// </summary>
    public FakeGeminiFileApi FakeVideoImport { get; } = new();

    private bool _dbCreated;

    /// <summary>
    /// Por defecto los tests desactivan el rate limiter (GetNoLimiter) para no acoplar
    /// la lógica de negocio a los límites. Las suites que verifican el propio rate-limiting
    /// (p. ej. <c>BuilderRateLimitTests</c>) sobreescriben esto a <c>false</c> para dejar
    /// activas las políticas reales de <see cref="RateLimitingExtensions"/>, y ajustan los
    /// límites vía <c>UseSetting</c>.
    /// </summary>
    protected virtual bool DisableRateLimiting => true;

    static ApiFixture()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", "");
        Environment.SetEnvironmentVariable("FIREBASE_PROJECT_ID", TestFirebaseProjectId);
        Environment.SetEnvironmentVariable("JWT_SECRET", TestJwtSecret);
        Environment.SetEnvironmentVariable("APPLE_BUNDLE_ID", TestAppleBundleId);
        Environment.SetEnvironmentVariable("GOOGLE_CLIENT_ID", TestGoogleClientId);
        // Gemini key inyectada como variable de entorno para que AiProviderService no tire
        // por "API Key missing" y siga el flujo HTTP (que atrapa nuestro FakeGeminiHandler).
        Environment.SetEnvironmentVariable("Gemini__ApiKey", "test-gemini-key");
        // Mapbox key — valor no vacío para que MapboxRoutingService no cortocircuite.
        Environment.SetEnvironmentVariable("Mapbox__AccessToken", "test-mapbox-token");
        // PostHog key — no vacío para que PostHogService no cortocircuite con el guard de API key.
        Environment.SetEnvironmentVariable("PostHog__ApiKey", "test-posthog-key");
        // Import de vídeo: sin espera entre polls de files.get para que los tests del loop
        // PROCESSING→ACTIVE no bloqueen (en prod el default es 1000ms).
        Environment.SetEnvironmentVariable("Import__FilePollDelayMs", "0");
    }

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Pre-create the `vector` extension BEFORE any NpgsqlDataSource is built.
        // Npgsql populates its pg_type cache on the first connection of each DataSource;
        // if the extension is created later (inside an EF migration), the cache stays
        // stale and writes of Pgvector.Vector parameters fail with
        // "Cannot resolve 'vector' to a fully qualified datatype name."
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            // Remove all production DbContext and factory registrations (RemoveAll tolerates
            // 0 or N descriptors, safe even if a future EF Core upgrade registers multiples).
            services.RemoveAll<DbContextOptions<LocalListDbContext>>();
            services.RemoveAll<IDbContextFactory<LocalListDbContext>>();

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString());
            dataSourceBuilder.UseVector();
            var dataSource = dataSourceBuilder.Build();

            services.AddDbContext<LocalListDbContext>(options =>
                options.UseNpgsql(dataSource, npg => npg.UseVector()));

            // Scoped factory mirrors the prod registration: each concurrent prefetch task
            // creates its own DbContext, avoiding EF Core concurrent-operation errors.
            services.AddDbContextFactory<LocalListDbContext>(options =>
                options.UseNpgsql(dataSource, npg => npg.UseVector()), ServiceLifetime.Scoped);

            // Replace TimeProvider.System with FakeTimeProvider
            var timeDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(TimeProvider));
            if (timeDescriptor is not null) services.Remove(timeDescriptor);

            services.AddSingleton<TimeProvider>(FakeTime);

            // Override Firebase scheme to use test RSA key instead of real Firebase JWKS.
            // Usamos IssuerSigningKeyResolver (en vez de IssuerSigningKey fijo) para
            // evitar que algún IPostConfigureOptions posterior lo machaque durante la
            // construcción del pipeline — visto en ejecuciones paralelas de múltiples
            // WebApplicationFactory simultáneas. El resolver se evalúa por cada
            // request y SIEMPRE devuelve nuestra RSA key.
            services.PostConfigure<JwtBearerOptions>("Firebase", options =>
            {
                options.Authority = null;
                options.ConfigurationManager = null;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = $"https://securetoken.google.com/{TestFirebaseProjectId}",
                    ValidateAudience = true,
                    ValidAudience = TestFirebaseProjectId,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new RsaSecurityKey(_testRsa),
                    IssuerSigningKeyResolver = (_, _, _, _) => new[] { new RsaSecurityKey(_testRsa) }
                };
            });

            // Replace Apple/Google ID-token validators with in-process fakes
            // (real ones hit Apple/Google JWKS endpoints — not viable in tests).
            var appleDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAppleIdTokenValidator));
            if (appleDescriptor is not null) services.Remove(appleDescriptor);
            services.AddSingleton<IAppleIdTokenValidator>(FakeApple);

            var googleDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IGoogleIdTokenValidator));
            if (googleDescriptor is not null) services.Remove(googleDescriptor);
            services.AddSingleton<IGoogleIdTokenValidator>(FakeGoogle);

            // Sustituir el HttpMessageHandler de los servicios solo-Gemini por el fake.
            // FakeGemini.Responder se configura en cada escenario de test.
            services.AddHttpClient<IPlaceTranslatorService, PlaceTranslatorService>()
                .ConfigurePrimaryHttpMessageHandler(_ => FakeGemini);
            services.AddHttpClient<IDescriptionGeneratorService, DescriptionGeneratorService>()
                .ConfigurePrimaryHttpMessageHandler(_ => FakeGemini);

            // La cadena LLM (SlotExtractorService + PreferenceExtractorService) usa named
            // clients "llm-{provider}". En tests solo Gemini__ApiKey está configurada, así
            // que la cadena efectiva es [gemini] y el comportamiento es idéntico al previo.
            // Tests de fallback derivan el host con UseSetting("OpenAI:ApiKey", ...) para
            // activar el segundo provider, atrapado por FakeOpenAi.
            // Registry scoped en tests (singleton en prod): el estado del circuit breaker
            // no debe filtrarse entre tests — 3 tests seguidos con Gemini en 503 abrirían
            // el circuito para el resto de la suite (FakeTime nunca avanza el cooldown).
            services.RemoveAll<LocalList.API.NET.Shared.AI.Llm.LlmProviderHealthRegistry>();
            services.AddScoped<LocalList.API.NET.Shared.AI.Llm.LlmProviderHealthRegistry>();

            services.AddHttpClient("llm-gemini")
                .ConfigurePrimaryHttpMessageHandler(_ => FakeGemini);
            services.AddHttpClient("llm-openai")
                .ConfigurePrimaryHttpMessageHandler(_ => FakeOpenAi);
            services.AddHttpClient("llm-mistral")
                .ConfigurePrimaryHttpMessageHandler(_ => FakeOpenAi);
            services.AddHttpClient("llm-anthropic")
                .ConfigurePrimaryHttpMessageHandler(_ => FakeOpenAi);

            services.AddHttpClient<EmbeddingService>()
                .ConfigurePrimaryHttpMessageHandler(_ => FakeEmbeddings);

            // Import de vídeo: el GeminiFileClient real y el HttpClient de VideoExtractionService
            // apuntan al mismo fake, que simula todo el ciclo File API + generateContent.
            services.AddHttpClient<IGeminiFileClient, GeminiFileClient>()
                .ConfigurePrimaryHttpMessageHandler(_ => FakeVideoImport);
            services.AddHttpClient<VideoExtractionService>()
                .ConfigurePrimaryHttpMessageHandler(_ => FakeVideoImport);

            services.AddHttpClient<IRoutingService, MapboxRoutingService>()
                .ConfigurePrimaryHttpMessageHandler(_ => FakeMapbox);

            services.AddHttpClient<PostHogService>()
                .ConfigurePrimaryHttpMessageHandler(_ => FakePostHog);

            // Replace IGooglePlacesService with in-process fake — avoids real HTTP calls
            // and lets tests control resolution + details results via FakeGooglePlaces.
            var googlePlacesDesc = services.Where(d => d.ServiceType == typeof(IGooglePlacesService)).ToList();
            foreach (var d in googlePlacesDesc) services.Remove(d);
            services.AddSingleton<IGooglePlacesService>(FakeGooglePlaces);

            // Disable rate limiting in tests (default). Las suites que testean el propio
            // rate-limiting ponen DisableRateLimiting=false para conservar las políticas
            // reales registradas por AddRateLimitingPolicies.
            if (!DisableRateLimiting) return;

            var rateLimiterDescriptors = services
                .Where(d => d.ServiceType == typeof(IConfigureOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>))
                .ToList();
            foreach (var d in rateLimiterDescriptors) services.Remove(d);

            services.AddRateLimiter(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<Microsoft.AspNetCore.Http.HttpContext, string>(
                    _ => RateLimitPartition.GetNoLimiter("test"));
                options.AddPolicy("AuthLimit", context =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
                options.AddPolicy("BuilderLimit", context =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
                options.AddPolicy("AdminLimit", context =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
                options.AddPolicy("WaitlistLimit", context =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
                options.AddPolicy("CitySearchLimit", context =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
                options.AddPolicy("CityCreateLimit", context =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
                options.AddPolicy("ChatTurnLimit", context =>
                    RateLimitPartition.GetNoLimiter(string.Empty));
            });
        });
    }

    private void EnsureDb()
    {
        if (_dbCreated) return;
        _dbCreated = true;
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalListDbContext>();
        db.Database.Migrate();
    }

    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();
        EnsureDb();
        return client;
    }

    /// <summary>
    /// Creates a Firebase-style RS256 JWT for testing.
    /// </summary>
    public string CreateToken(string firebaseUid, string email = "test@test.com")
    {
        var key = new RsaSecurityKey(_testRsa);
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, firebaseUid),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("email_verified", "true"),
            new Claim("user_id", firebaseUid)
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = FakeTime.GetUtcNow().AddHours(1).UtcDateTime,
            Issuer = $"https://securetoken.google.com/{TestFirebaseProjectId}",
            Audience = TestFirebaseProjectId,
            SigningCredentials = credentials
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(tokenDescriptor);
        return handler.WriteToken(token);
    }

    public HttpClient CreateAuthenticatedClient(Guid userId, string firebaseUid, string email = "test@test.com")
    {
        var client = CreateClient();
        var token = CreateToken(firebaseUid, email);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Convenience overload: seeds a user with the given ID and firebase_uid, returns an authenticated client.
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedClientWithUser(
        Guid userId, string firebaseUid, string email = "test@test.com", string role = "user")
    {
        var db = GetDbContext();
        var existing = await db.Users.FindAsync(userId);
        if (existing == null)
        {
            db.Users.Add(new LocalList.API.NET.Shared.Data.Entities.User
            {
                Id = userId,
                Email = email,
                FirebaseUid = firebaseUid,
                Role = role
            });
            await db.SaveChangesAsync();
        }
        return CreateAuthenticatedClient(userId, firebaseUid, email);
    }

    /// <summary>
    /// Cliente autenticado con un token de la APP (HS256, AppScheme) — a diferencia de
    /// <see cref="CreateAuthenticatedClientWithUser"/>, que emite un token Firebase (RS256).
    /// El rate-limit de generación solo concede el bucket alto a tokens AppScheme, así que
    /// los tests de ese bucket deben usar este helper.
    /// </summary>
    public async Task<HttpClient> CreateAppAuthenticatedClientWithUser(
        Guid userId, string email)
    {
        var db = GetDbContext();
        var existing = await db.Users.FindAsync(userId);
        if (existing == null)
        {
            db.Users.Add(new LocalList.API.NET.Shared.Data.Entities.User
            {
                Id = userId,
                Email = email,
                FirebaseUid = "app-" + userId,
                Role = "user"
            });
            await db.SaveChangesAsync();
        }
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateAppToken(userId, email));
        return client;
    }

    public LocalListDbContext GetDbContext()
    {
        EnsureDb();
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<LocalListDbContext>();
    }

    /// <summary>
    /// Creates an HS256 JWT signed with the test JWT_SECRET — mirrors what
    /// <see cref="JwtTokenService"/> emits for the real app flow.
    /// </summary>
    public string CreateAppToken(Guid userId, string email, string tier = "free")
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = FakeTime.GetUtcNow().UtcDateTime;
        var token = new JwtSecurityToken(
            issuer: JwtTokenService.Issuer,
            audience: JwtTokenService.Audience,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, email),
                new Claim("tier", tier)
            },
            notBefore: now,
            expires: now.AddMinutes(15),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public class FakeAppleIdTokenValidator : IAppleIdTokenValidator
{
    public Dictionary<string, OAuthClaims> Tokens { get; } = new();

    public Task<OAuthClaims?> ValidateAsync(string idToken, CancellationToken ct) =>
        Task.FromResult(Tokens.TryGetValue(idToken, out var claims) ? claims : null);
}

public class FakeGoogleIdTokenValidator : IGoogleIdTokenValidator
{
    public Dictionary<string, OAuthClaims> Tokens { get; } = new();

    public Task<OAuthClaims?> ValidateAsync(string idToken, CancellationToken ct) =>
        Task.FromResult(Tokens.TryGetValue(idToken, out var claims) ? claims : null);
}

/// <summary>
/// Handler HTTP fake para <c>EmbeddingService</c>. Por defecto responde a
/// <c>:batchEmbedContents</c> con embeddings deterministas de 768 dims
/// derivados del hash del texto, suficiente para assertions de flujo.
/// Los tests pueden sobreescribir <see cref="Responder"/> para forzar
/// errores o formatos específicos.
/// </summary>
public class FakeEmbeddingHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }
    public List<HttpRequestMessage> Calls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        if (Responder is not null) return Responder(request);

        var body = await (request.Content?.ReadAsStringAsync(cancellationToken) ?? Task.FromResult("{}"));
        using var doc = JsonDocument.Parse(body);
        var count = doc.RootElement.TryGetProperty("requests", out var reqs) ? reqs.GetArrayLength() : 0;

        var embeddings = new List<object>(count);
        foreach (var req in reqs.EnumerateArray())
        {
            var text = req.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            var seed = text.Aggregate(0, (acc, c) => unchecked(acc * 31 + c));
            var rng = new Random(seed);
            var values = new float[EmbeddingService.Dimensions];
            for (var i = 0; i < values.Length; i++) values[i] = (float)(rng.NextDouble() * 2 - 1);
            embeddings.Add(new { values });
        }

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { embeddings }), Encoding.UTF8, "application/json")
        };
        return response;
    }
}

/// <summary>
/// Handler HTTP fake que intercepta las llamadas a Mapbox Directions desde
/// <c>MapboxRoutingService</c>. Los tests ajustan <see cref="Responder"/> para
/// devolver distintos cuerpos/estados. Por defecto devuelve una respuesta Mapbox
/// mínima válida con una polyline de prueba para no bloquear tests no relacionados.
/// </summary>
public class FakeMapboxHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

    /// <summary>
    /// Async variant — takes priority over <see cref="Responder"/>. Use when the test needs
    /// to introduce real latency (e.g. <c>await Task.Delay(50, ct)</c>) so that concurrent
    /// pre-fetch tasks are truly in-flight simultaneously.
    /// </summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncResponder { get; set; }

    public List<HttpRequestMessage> Calls { get; } = new();

    private const string DefaultMapboxResponse = """
        {"routes":[{"geometry":"test_polyline","legs":[{"distance":500.0,"duration":300.0}],"distance":500.0,"duration":300.0}],"code":"Ok"}
        """;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Calls) { Calls.Add(request); }

        if (AsyncResponder is { } asyncR)
            return await asyncR(request, cancellationToken);

        if (Responder is { } responder)
            return responder(request);

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(DefaultMapboxResponse, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>
/// In-process fake implementation of <see cref="IGooglePlacesService"/>.
/// Configure <see cref="DetailsByPlaceId"/> and <see cref="ResolvedByUrl"/> dictionaries before
/// each test. <see cref="SearchResponder"/> remains a Func since search has no natural key.
/// </summary>
public class FakeGooglePlacesService : IGooglePlacesService
{
    public Func<string, CancellationToken, Task<List<GooglePlacePreview>?>>? SearchResponder { get; set; }
    public Dictionary<string, GooglePlaceDetails> DetailsByPlaceId { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> ResolvedByUrl { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<List<GooglePlacePreview>?> SearchAsync(string textQuery, CancellationToken ct, decimal? lat = null, decimal? lng = null) =>
        SearchResponder is not null ? SearchResponder(textQuery, ct) : Task.FromResult<List<GooglePlacePreview>?>(null);

    public Task<GooglePlaceDetails?> GetDetailsAsync(string placeId, CancellationToken ct) =>
        Task.FromResult(DetailsByPlaceId.TryGetValue(placeId, out var d) ? d : (GooglePlaceDetails?)null);

    public Task<string?> ResolvePlaceIdFromUrlAsync(string input, CancellationToken ct) =>
        Task.FromResult(ResolvedByUrl.TryGetValue(input, out var id) ? id : null);

    public void Reset()
    {
        SearchResponder = null;
        DetailsByPlaceId.Clear();
        ResolvedByUrl.Clear();
    }
}

/// <summary>
/// Handler HTTP fake que intercepta llamadas de <see cref="PostHogService"/> a /capture/.
/// Recoge los cuerpos de las peticiones en <see cref="CapturedBodies"/> para que los tests
/// puedan verificar qué eventos fueron disparados. Responde siempre 200 OK.
/// </summary>
public class FakePostHogHandler : HttpMessageHandler
{
    private readonly ConcurrentBag<string> _bodies = new();

    public IReadOnlyCollection<string> CapturedBodies => _bodies;

    public void Reset() => _bodies.Clear();

    /// <summary>Returns all captured bodies deserialized as JSON documents (caller must dispose).</summary>
    public List<JsonDocument> ParsedEvents()
        => _bodies.Select(b => JsonDocument.Parse(b)).ToList();

    public bool HasEvent(string eventName) =>
        _bodies.Any(b =>
        {
            using var doc = JsonDocument.Parse(b);
            return doc.RootElement.TryGetProperty("event", out var e) &&
                   e.GetString() == eventName;
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await (request.Content?.ReadAsStringAsync(cancellationToken) ?? Task.FromResult("{}"));
        _bodies.Add(body);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":1}", System.Text.Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>
/// Handler HTTP fake que intercepta las llamadas a Gemini desde los servicios Gemini
/// (PreferenceExtractorService, PlaceTranslatorService, DescriptionGeneratorService).
/// Los tests ajustan <see cref="Responder"/> para devolver distintos cuerpos/estados
/// (200 OK, 502, JSON malformado…).
/// Si <see cref="Responder"/> es null, devuelve 200 con una respuesta Gemini vacía.
/// </summary>
public class FakeGeminiHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

    public List<HttpRequestMessage> Calls { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        var responder = Responder;
        if (responder is null)
        {
            // Respuesta por defecto: 200 con un candidate vacío, fuerza fallback a keywords
            // sólo si el test no ha configurado nada. Así los tests que no tocan Gemini
            // no ven 500 del HttpClient.
            var fallback = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{}\"}]},\"finishReason\":\"STOP\"}]," +
                    "\"usageMetadata\":{\"promptTokenCount\":100,\"candidatesTokenCount\":50,\"thoughtsTokenCount\":10}}",
                    System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(fallback);
        }
        return Task.FromResult(responder(request));
    }
}

/// <summary>
/// Handler HTTP fake para los providers OpenAI-compatible y Anthropic de la cadena LLM.
/// Los tests de fallback ajustan <see cref="Responder"/> con el shape de chat/completions
/// (o /v1/messages para Anthropic). Si Responder es null devuelve 503, de modo que un
/// provider activado por error en un test no responda con éxito silencioso.
/// </summary>
public class FakeOpenAiHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

    /// <summary>Thread-safe: el mismo handler sirve a varios named clients (llm-openai/mistral/anthropic) en paralelo.</summary>
    public ConcurrentBag<HttpRequestMessage> Calls { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        var responder = Responder;
        if (responder is null)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":\"fake provider not configured\"}",
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
        return Task.FromResult(responder(request));
    }
}

/// <summary>
/// Simula la Gemini File API (subida resumable, files.get, files.delete) y generateContent
/// multimodal para el import de vídeo. Enruta por método + URL + cabecera de comando de subida.
///
/// Knobs por test:
///  - <see cref="DurationSec"/>: duración que devuelve files.get (para probar VideoTooLong).
///  - <see cref="PollActiveAfter"/>: nº de polls en PROCESSING antes de pasar a ACTIVE (loop de poll).
///  - <see cref="FailProcessing"/>: si true, files.get devuelve state=FAILED.
///  - <see cref="GenerateContentResponder"/>: cuerpo de generateContent (por defecto un sitio válido).
///
/// Observabilidad para asserts:
///  - <see cref="UploadStarted"/> / <see cref="UploadFinalized"/> / <see cref="GenerateContentCalled"/>.
///  - <see cref="DeleteCalledFor"/>: nombres de fichero borrados (clave del test de NO retención).
/// </summary>
public class FakeGeminiFileApi : HttpMessageHandler
{
    private const string FileName = "files/test-video-abc";
    private const string FileUri = "https://generativelanguage.googleapis.com/v1beta/files/test-video-abc";

    public double DurationSec { get; set; } = 12.0;
    public int PollActiveAfter { get; set; } = 0;
    public bool FailProcessing { get; set; } = false;
    public Func<HttpRequestMessage, HttpResponseMessage>? GenerateContentResponder { get; set; }

    public bool UploadStarted { get; private set; }
    public bool UploadFinalized { get; private set; }
    public bool GenerateContentCalled { get; private set; }
    public List<string> DeleteCalledFor { get; } = new();

    private int _pollCount;

    /// <summary>Cuerpo de generateContent por defecto: un único sitio limpio y válido.</summary>
    public static string DefaultPlacesJson => """
        {
          "city": "Miami",
          "country": "USA",
          "language": "en",
          "places": [
            { "name": "Sunny Rooftop", "descriptor": "rooftop bar en Wynwood", "category": "nightlife", "evidence": "ocr", "timestampSec": 12 }
          ],
          "vibes": ["sunset", "cocktails"],
          "confidence": 0.82
        }
        """;

    public HttpResponseMessage GenerateContentOk(string placesJson, int inputTokens = 17400, int outputTokens = 240, int thinkingTokens = 0)
    {
        var escaped = JsonSerializer.Serialize(placesJson); // JSON-encode the model text
        var body = $$"""
            {
              "candidates": [ { "content": { "parts": [ { "text": {{escaped}} } ] }, "finishReason": "STOP" } ],
              "usageMetadata": { "promptTokenCount": {{inputTokens}}, "candidatesTokenCount": {{outputTokens}}, "thoughtsTokenCount": {{thinkingTokens}} }
            }
            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    public void Reset()
    {
        DurationSec = 12.0;
        PollActiveAfter = 0;
        FailProcessing = false;
        GenerateContentResponder = null;
        UploadStarted = false;
        UploadFinalized = false;
        GenerateContentCalled = false;
        DeleteCalledFor.Clear();
        _pollCount = 0;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        // 1. Inicio de la subida resumable → devuelve la URL de sesión en la cabecera.
        if (request.Method == HttpMethod.Post && url.Contains("/upload/v1beta/files"))
        {
            UploadStarted = true;
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "text/plain")
            };
            resp.Headers.Add("X-Goog-Upload-URL", "https://upload.internal/session/test-upload-1");
            return Task.FromResult(resp);
        }

        // 2. Subida + finalize a la URL de sesión → devuelve el recurso file (PROCESSING).
        if (request.Method == HttpMethod.Post && url.Contains("upload.internal/session"))
        {
            UploadFinalized = true;
            var body = $$"""
                { "file": { "name": "{{FileName}}", "uri": "{{FileUri}}", "mimeType": "video/mp4", "sizeBytes": "1024", "state": "PROCESSING" } }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        // 3. generateContent multimodal.
        if (request.Method == HttpMethod.Post && url.Contains(":generateContent"))
        {
            GenerateContentCalled = true;
            var responder = GenerateContentResponder;
            return Task.FromResult(responder is not null ? responder(request) : GenerateContentOk(DefaultPlacesJson));
        }

        // 4. files.delete — clave del invariante de NO retención.
        if (request.Method == HttpMethod.Delete && url.Contains("/v1beta/files/"))
        {
            lock (DeleteCalledFor) { DeleteCalledFor.Add(url.Split("/v1beta/")[^1]); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }

        // 5. files.get (poll de estado).
        if (request.Method == HttpMethod.Get && url.Contains("/v1beta/files/"))
        {
            _pollCount++;
            string state;
            if (FailProcessing) state = "FAILED";
            else state = _pollCount > PollActiveAfter ? "ACTIVE" : "PROCESSING";

            var durationField = state == "ACTIVE"
                ? $$""", "videoMetadata": { "videoDuration": "{{DurationSec.ToString(System.Globalization.CultureInfo.InvariantCulture)}}s" }"""
                : "";
            var body = $$"""
                { "name": "{{FileName}}", "uri": "{{FileUri}}", "mimeType": "video/mp4", "state": "{{state}}"{{durationField}} }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":\"fake file api: unrouted request\"}", Encoding.UTF8, "application/json")
        });
    }
}
