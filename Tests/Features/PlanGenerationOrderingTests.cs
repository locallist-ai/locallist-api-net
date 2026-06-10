using LocalList.API.NET.Features.Builder;
using LocalList.API.NET.Features.Builder.Services;
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
        // Arrange: 3 places inserted in REVERSE Id order so that heap-scan order
        // would return them largest-first if there were no ORDER BY.
        var city = "TestCity_OrderBy_" + Guid.NewGuid().ToString("N")[..8];

        var idSmall  = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var idMedium = Guid.Parse("50000000-0000-0000-0000-000000000001");
        var idLarge  = Guid.Parse("f0000000-0000-0000-0000-000000000001");

        var db = fixture.GetDbContext();

        // Insert largest → smallest so naive heap-scan returns descending
        db.Places.Add(new Place { Id = idLarge,  Name = "C", Category = "food", WhyThisPlace = "t", Status = "published", City = city });
        db.Places.Add(new Place { Id = idSmall,  Name = "A", Category = "food", WhyThisPlace = "t", Status = "published", City = city });
        db.Places.Add(new Place { Id = idMedium, Name = "B", Category = "food", WhyThisPlace = "t", Status = "published", City = city });
        await db.SaveChangesAsync();

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

        // Assert: result must be sorted by Id ascending regardless of insertion order
        Assert.Equal(3, result.Count);
        Assert.Equal(idSmall,  result[0].Id);
        Assert.Equal(idMedium, result[1].Id);
        Assert.Equal(idLarge,  result[2].Id);
    }
}
