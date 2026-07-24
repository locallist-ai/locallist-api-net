using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Migrations;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// T4: verifica que la sql de la migración <c>PurgeGooglePhotoUrlsWithKey</c> limpia el
/// HISTÓRICO de <c>places.photos</c> escrito antes de T3 (URLs
/// <c>places.googleapis.com/.../media?...key=SECRET</c>), conservando cualquier URL externa
/// legítima que conviva en el mismo array. Cierra MINOR-2 del review de T3.
///
/// El fixture ya aplica TODAS las migraciones (incluida esta, vía <c>Database.Migrate()</c>
/// en <c>ApiFixture.EnsureDb</c>) contra un contenedor fresco ANTES de que estos tests
/// siembren datos, así que para poder observar el efecto de la migración sobre filas
/// "sucias" sembramos DESPUÉS del up y re-ejecutamos la MISMA sql constante
/// (<see cref="PurgeGooglePhotoUrlsWithKey.PurgeGoogleKeyedPhotosSql"/>) que el <c>Up</c> de
/// la migración ejecuta (no una reimplementación paralela), exactamente lo que habría hecho
/// el up si esas filas hubieran existido antes del deploy.
/// </summary>
public class PurgeGooglePhotoUrlsMigrationTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const string LeakedGoogleUrl =
        "https://places.googleapis.com/v1/places/xyz/photos/abc/media?maxWidthPx=1600&key=SECRET";
    private const string CleanExternalUrl1 = "https://legit.example/pic.jpg";
    private const string CleanExternalUrl2 = "https://another.example/photo2.jpg";

    [Fact]
    public async Task Migration_RemovesGoogleUrl_KeepsExternalUrl_InMixedArray()
    {
        var db = fixture.GetDbContext();
        var id = Guid.NewGuid();
        db.Places.Add(NewPlace(id, "Mixed", [LeakedGoogleUrl, CleanExternalUrl1]));
        await db.SaveChangesAsync();

        await RunMigrationSqlAsync(db);

        var saved = await fixture.GetDbContext().Places.AsNoTracking().FirstAsync(p => p.Id == id);
        Assert.Equal([CleanExternalUrl1], saved.Photos);
    }

    [Fact]
    public async Task Migration_OnlyGoogleUrl_ResultsInNullPhotos()
    {
        var db = fixture.GetDbContext();
        var id = Guid.NewGuid();
        db.Places.Add(NewPlace(id, "OnlyGoogle", [LeakedGoogleUrl]));
        await db.SaveChangesAsync();

        await RunMigrationSqlAsync(db);

        var saved = await fixture.GetDbContext().Places.AsNoTracking().FirstAsync(p => p.Id == id);
        Assert.Null(saved.Photos);
    }

    [Fact]
    public async Task Migration_OnlyExternalUrls_LeftIntact()
    {
        var db = fixture.GetDbContext();
        var id = Guid.NewGuid();
        List<string> original = [CleanExternalUrl1, CleanExternalUrl2];
        db.Places.Add(NewPlace(id, "OnlyExternal", original));
        await db.SaveChangesAsync();

        await RunMigrationSqlAsync(db);

        var saved = await fixture.GetDbContext().Places.AsNoTracking().FirstAsync(p => p.Id == id);
        Assert.Equal(original, saved.Photos);
    }

    [Fact]
    public async Task Migration_NullPhotos_LeftUntouched()
    {
        var db = fixture.GetDbContext();
        var id = Guid.NewGuid();
        db.Places.Add(NewPlace(id, "NullPhotos", null));
        await db.SaveChangesAsync();

        await RunMigrationSqlAsync(db);

        var saved = await fixture.GetDbContext().Places.AsNoTracking().FirstAsync(p => p.Id == id);
        Assert.Null(saved.Photos);
    }

    /// <summary>Re-ejecutar la sql sobre una fila ya limpia no debe alterarla (idempotencia).</summary>
    [Fact]
    public async Task Migration_IsIdempotent_RunningTwiceIsSafe()
    {
        var db = fixture.GetDbContext();
        var id = Guid.NewGuid();
        db.Places.Add(NewPlace(id, "Idempotent", [LeakedGoogleUrl, CleanExternalUrl1]));
        await db.SaveChangesAsync();

        await RunMigrationSqlAsync(db);
        await RunMigrationSqlAsync(db);

        var saved = await fixture.GetDbContext().Places.AsNoTracking().FirstAsync(p => p.Id == id);
        Assert.Equal([CleanExternalUrl1], saved.Photos);
    }

    private static Place NewPlace(Guid id, string label, List<string>? photos) => new()
    {
        Id = id,
        Name = $"Purge Test {label} {id:N}",
        Category = "Food",
        City = "Miami",
        WhyThisPlace = "test",
        Status = "in_review",
        Photos = photos,
    };

    private static Task RunMigrationSqlAsync(LocalListDbContext db) =>
        db.Database.ExecuteSqlRawAsync(PurgeGooglePhotoUrlsWithKey.PurgeGoogleKeyedPhotosSql);
}
