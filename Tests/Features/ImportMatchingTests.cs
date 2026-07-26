using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LocalList.API.NET.Features.Import;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// F2 T3 — matching determinista de candidatos extraídos contra el catálogo curado
/// (<see cref="ImportMatchingService"/>) sobre DB real (ApiFixture = Testcontainers PostgreSQL,
/// catálogo sembrado). Cada test usa una CIUDAD única (GUID) para aislarse del catálogo compartido
/// del contenedor — el matcher filtra por ciudad normalizada, así que places de otros tests nunca
/// interfieren. La DB nunca se mockea. El test (i) es end-to-end vía <c>POST /import/video</c>.
/// </summary>
public class ImportMatchingTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    public void Dispose() => fixture.FakeVideoImport.Reset();

    // ── helpers ────────────────────────────────────────────────────────────────

    private static string City() => "City" + Guid.NewGuid().ToString("N")[..12];
    private static ExtractedVideoPlace Cand(string name) => new(name, null, null, "ocr", null);

    private async Task<Guid> Seed(string name, string city, string status = "published", Guid? id = null)
    {
        var db = fixture.GetDbContext();
        var pid = id ?? Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = pid, Name = name, City = city, Status = status,
            Category = "food", WhyThisPlace = "t",
        });
        await db.SaveChangesAsync();
        return pid;
    }

    private async Task<IReadOnlyList<MatchedImportPlace>> Match(
        string? city, params ExtractedVideoPlace[] candidates)
    {
        _ = fixture.GetDbContext(); // asegura migraciones
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ImportMatchingService>();
        return await svc.MatchAsync(city, candidates, CancellationToken.None);
    }

    // ── (a) igualdad normalizada (case + diacríticos) → high ─────────────────────
    [Fact]
    public async Task ExactNormalized_CaseAndDiacritics_MatchesHigh()
    {
        var city = City();
        var id = await Seed("Café Versailles", city);

        var m = (await Match(city, Cand("cafe versailles")))[0];

        Assert.Equal(id, m.MatchedPlaceId);
        Assert.Equal("Café Versailles", m.MatchedPlaceName);
        Assert.Equal(ImportMatchingService.ConfidenceHigh, m.MatchConfidence);
    }

    // ── (b) contains (sufijo ruidoso) → high ─────────────────────────────────────
    [Fact]
    public async Task Contains_LongerNameWithSuffix_MatchesHigh()
    {
        var city = City();
        var id = await Seed("Joe's Stone Crab Restaurant", city);

        var m = (await Match(city, Cand("Joe's Stone Crab")))[0];

        Assert.Equal(id, m.MatchedPlaceId);
        Assert.Equal(ImportMatchingService.ConfidenceHigh, m.MatchConfidence);
    }

    // ── (c) solape de tokens (no contiguo) → medium ──────────────────────────────
    [Fact]
    public async Task TokenOverlap_NonContiguous_MatchesMedium()
    {
        var city = City();
        var id = await Seed("Wynwood Walls Miami", city);

        // "wynwood art walls" comparte {wynwood, walls} (2/3) pero NO es un run contiguo del
        // nombre del place → no llega a high (contains), sí a medium.
        var m = (await Match(city, Cand("Wynwood Art Walls")))[0];

        Assert.Equal(id, m.MatchedPlaceId);
        Assert.Equal(ImportMatchingService.ConfidenceMedium, m.MatchConfidence);
    }

    // ── (d) un único token genérico común → NO match ─────────────────────────────
    [Fact]
    public async Task SingleGenericTokenInCommon_DoesNotMatch()
    {
        var city = City();
        await Seed("Sunny Beach", city);

        // Comparten solo "beach" (1 token) → por debajo del mínimo de 2 → sin match.
        var m = (await Match(city, Cand("Miami Beach")))[0];

        Assert.Null(m.MatchedPlaceId);
        Assert.Null(m.MatchConfidence);
    }

    // ── (e) homónimo en OTRA ciudad → no cruza el scope de ciudad ─────────────────
    [Fact]
    public async Task Homonym_OtherCity_ExcludedByCityScope()
    {
        var cityA = City();
        var cityB = City();
        var idA = await Seed("Twin Peak", cityA);
        var idB = await Seed("Twin Peak", cityB); // mismo nombre, otra ciudad

        var m = (await Match(cityA, Cand("Twin Peak")))[0];

        Assert.Equal(idA, m.MatchedPlaceId);      // matchea el de SU ciudad
        Assert.NotEqual(idB, m.MatchedPlaceId);   // nunca el homónimo de la otra
        Assert.Equal(ImportMatchingService.ConfidenceHigh, m.MatchConfidence);
    }

    // ── (f) place draft/rejected → excluido (solo published) ─────────────────────
    [Fact]
    public async Task DraftAndRejectedPlaces_Excluded()
    {
        var city = City();
        await Seed("Hidden Speakeasy", city, status: "draft");
        await Seed("Hidden Speakeasy", city, status: "rejected");

        var m = (await Match(city, Cand("Hidden Speakeasy")))[0];

        Assert.Null(m.MatchedPlaceId);
        Assert.Null(m.MatchConfidence);
    }

    // ── (g) ciudad no en catálogo / ausente → todos unmatched, sin error ─────────
    [Fact]
    public async Task CityNotInCatalog_AllUnmatched_NoError()
    {
        var results = await Match(City(), Cand("Anywhere Bar"), Cand("Nowhere Cafe"));
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Null(r.MatchedPlaceId));
    }

    [Fact]
    public async Task NullDetectedCity_AllUnmatched_NoError()
    {
        var results = await Match(null, Cand("Sunny Rooftop"));
        Assert.Single(results);
        Assert.Null(results[0].MatchedPlaceId);
    }

    // ── (h) empates → resultado determinista (mismo Id en 2 runs; el menor Id) ────
    [Fact]
    public async Task Ties_AreDeterministic_PickLowestId()
    {
        var city = City();
        var id1 = await Seed("Duplicate Diner", city);
        var id2 = await Seed("Duplicate Diner", city); // nombre idéntico → empate exacto
        var expected = new[] { id1, id2 }.OrderBy(x => x).First();

        var run1 = (await Match(city, Cand("Duplicate Diner")))[0].MatchedPlaceId;
        var run2 = (await Match(city, Cand("Duplicate Diner")))[0].MatchedPlaceId;

        Assert.Equal(run1, run2);        // reproducible
        Assert.Equal(expected, run1);    // desempate por Id (orden total)
        Assert.Equal(ImportMatchingService.ConfidenceHigh, (await Match(city, Cand("Duplicate Diner")))[0].MatchConfidence);
    }

    // ── (i) integración end-to-end: import → matchedPlaceId + metric.num_matched ──
    [Fact]
    public async Task Endpoint_EndToEnd_ReturnsMatchedIds_PersistsNumMatched()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(uid, $"imp-match-{uid:N}@test.com", tier: "pro");

        var city = "Rivertown" + Guid.NewGuid().ToString("N")[..8];
        var matchedId = await Seed("Skyline Lounge", city);      // este SÍ está en catálogo
        // "Ghost Kitchen" NO se siembra → debe salir unmatched.

        // FakeGemini devuelve 2 candidatos en `city`: uno matchea, el otro no.
        fixture.FakeVideoImport.GenerateContentResponder = _ =>
            fixture.FakeVideoImport.GenerateContentOk($$"""
                {
                  "city": "{{city}}", "country": "USA", "language": "en",
                  "places": [
                    { "name": "Skyline Lounge", "descriptor": "great rooftop", "category": "nightlife", "evidence": "ocr", "timestampSec": 3 },
                    { "name": "Ghost Kitchen", "descriptor": "hidden gem", "category": "food", "evidence": "audio", "timestampSec": 9 }
                  ],
                  "vibes": ["fun"], "confidence": 0.7
                }
                """);

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[2048]);
        file.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(file, "video", "clip.mp4");

        var res = await client.PostAsync("/import/video?platform=self", form);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var places = body.GetProperty("places");
        Assert.Equal(2, places.GetArrayLength());

        // Candidato 1 → matcheado high con el place sembrado.
        Assert.Equal("Skyline Lounge", places[0].GetProperty("name").GetString());
        Assert.Equal(matchedId, places[0].GetProperty("matchedPlaceId").GetGuid());
        Assert.Equal("Skyline Lounge", places[0].GetProperty("matchedPlaceName").GetString());
        Assert.Equal("high", places[0].GetProperty("matchConfidence").GetString());

        // Candidato 2 → no está en catálogo: campos de match null (omitidos por WhenWritingNull).
        Assert.Equal("Ghost Kitchen", places[1].GetProperty("name").GetString());
        Assert.False(places[1].TryGetProperty("matchedPlaceId", out _));
        Assert.False(places[1].TryGetProperty("matchConfidence", out _));

        // Metric: num_matched = 1 (anotado en la MISMA fila del diagnóstico de extracción).
        var db = fixture.GetDbContext();
        var metric = await db.VideoImportMetrics
            .Where(m => m.City == city).OrderByDescending(m => m.CreatedAt).FirstAsync();
        Assert.Equal(2, metric.NumPlaces);
        Assert.Equal(1, metric.NumMatched);
    }
}
