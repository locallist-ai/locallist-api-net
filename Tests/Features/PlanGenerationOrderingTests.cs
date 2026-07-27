using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.AI.Services;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Integration test (Testcontainers + real Postgres) that guards the load-bearing
/// <c>OrderBy(p => p.Id)</c> in <see cref="PlanGenerationService.FallbackKeywordFilterAsync"/>.
///
/// Without this sort the candidate pool returned from the DB has an undefined order
/// (PostgreSQL heap scan order varies with vacuuming and concurrent writes), making
/// "same seed → same plan" non-deterministic across requests.
///
/// <b>Regression gate:</b> removing <c>.OrderBy(p => p.Id)</c> from the source query
/// will cause this test to fail because the DB does NOT return rows in insertion order.
/// </summary>
public class PlanGenerationOrderingTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task FallbackKeyword_CandidatePool_IsReturnedSortedByIdAscending()
    {
        // Arrange: n≥8 places whose Ids are known. GOTCHA del repo: EF Core reordena los INSERT de
        // un MISMO SaveChanges por PK, así que sembrar "en orden inverso" dentro de un solo
        // SaveChanges no prueba nada — EF los mandaría igualmente ordenados por Id y el heap-scan
        // saldría ascendente aunque NO hubiera ORDER BY (test vacuo). Para que el test MUERDA de
        // verdad, insertamos UNA FILA POR SaveChanges en orden ANTI-PK (descendente): así el orden
        // físico del heap es descendente y, sin el ORDER BY, la query devolvería descendente.
        var city = "TestCity_OrderBy_" + Guid.NewGuid().ToString("N")[..8];

        const int n = 8;
        // Ids deterministas y ordenables: 01..08 en el último byte.
        var ascendingIds = Enumerable.Range(1, n)
            .Select(i => Guid.Parse($"a0000000-0000-0000-0000-0000000000{i:D2}"))
            .ToList();

        var db = fixture.GetDbContext();
        // Inserta en orden DESCENDENTE, una fila por SaveChanges (EF no puede reordenar entre
        // SaveChanges distintos) → el heap queda en orden físico descendente.
        foreach (var id in Enumerable.Reverse(ascendingIds))
        {
            db.Places.Add(new Place
            {
                Id = id, Name = $"P{id.ToString()[^2..]}", Category = "food",
                WhyThisPlace = "t", Status = "published", City = city,
            });
            await db.SaveChangesAsync();
        }

        // Resolve PlanGenerationService from the DI container (real DB + fakes already wired)
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PlanGenerationService>();

        var prefs = new ExtractedPreferences
        {
            Days = 1,
            MaxStopsPerDay = 5,
            Categories = ["food"],
            GroupType = "couple",
        };

        // Act: call the internal method that contains the OrderBy
        var result = await svc.FallbackKeywordFilterAsync(city, prefs, CancellationToken.None);

        // Assert: la proyección debe salir ordenada por Id ASCENDENTE pese a la inserción anti-PK.
        // Quitar el .OrderBy(p => p.Id) de FallbackKeywordFilterAsync hace fallar esto (el heap-scan
        // devuelve descendente). Verificamos TODAS las posiciones, no solo el count.
        Assert.Equal(n, result.Count);
        Assert.Equal(ascendingIds, result.Select(p => p.Id).ToList());
    }
}
