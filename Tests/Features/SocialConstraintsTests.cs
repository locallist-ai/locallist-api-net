using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Social S1 (MINORs c/d): CHECK constraints de dominio a nivel de fila. DB real (Testcontainers):
/// probamos que Postgres RECHAZA los estados inválidos, no un validador en C#.
/// </summary>
public class SocialConstraintsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static bool IsCheckViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.CheckViolation;

    // ── (c) self-block rechazado por ck_user_blocks_no_self ──
    [Fact]
    public async Task UserBlock_SelfBlock_RejectedByDb()
    {
        var id = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = id, Email = $"selfblock-{id:N}@test.com", FirebaseUid = $"fb-selfblock-{id:N}" });
        await db.SaveChangesAsync();

        db.UserBlocks.Add(new UserBlock { BlockerId = id, BlockedId = id });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(IsCheckViolation(ex));
    }

    // ── (c) bloqueo entre usuarios DISTINTOS sí se permite ──
    [Fact]
    public async Task UserBlock_DifferentUsers_Allowed()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = a, Email = $"blka-{a:N}@test.com", FirebaseUid = $"fb-blka-{a:N}" });
        db.Users.Add(new User { Id = b, Email = $"blkb-{b:N}@test.com", FirebaseUid = $"fb-blkb-{b:N}" });
        await db.SaveChangesAsync();

        db.UserBlocks.Add(new UserBlock { BlockerId = a, BlockedId = b });
        await db.SaveChangesAsync(); // no lanza
    }

    // ── (d) visibility fuera de dominio rechazado por ck_plans_visibility_domain ──
    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("PUBLIC")] // case-sensitive: 'PUBLIC' no está en el dominio
    public async Task Plan_VisibilityOutOfDomain_RejectedByDb(string badVisibility)
    {
        var db = fixture.GetDbContext();
        db.Plans.Add(new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Bad Visibility",
            City = "Miami",
            Type = "custom",
            Visibility = badVisibility,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(IsCheckViolation(ex));
    }

    [Theory]
    [InlineData("private")]
    [InlineData("unlisted")]
    [InlineData("public")]
    public async Task Plan_VisibilityInDomain_Allowed(string visibility)
    {
        var db = fixture.GetDbContext();
        db.Plans.Add(new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Good Visibility",
            City = "Miami",
            Type = "custom",
            Visibility = visibility,
        });
        await db.SaveChangesAsync(); // no lanza
    }
}
