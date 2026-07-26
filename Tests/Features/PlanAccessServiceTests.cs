using LocalList.API.NET.Features.Social.Entities;
using LocalList.API.NET.Shared.Access;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Matriz de autorizacion de <see cref="IPlanAccessService"/> — el CIMIENTO del acceso social.
/// Cada rol contra un plan con el <see cref="PlanAccess"/> esperado. DB real (Testcontainers):
/// el servicio consulta Postgres, nunca se mockea.
/// </summary>
public class PlanAccessServiceTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private async Task<PlanAccess> Resolve(Guid planId, Guid? userId)
    {
        using var scope = fixture.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IPlanAccessService>();
        return await svc.GetAccessAsync(planId, userId, CancellationToken.None);
    }

    private async Task<Guid> SeedUser()
    {
        var id = Guid.NewGuid();
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = id, Email = $"acc-{id:N}@test.com", FirebaseUid = $"fb-{id:N}" });
        await db.SaveChangesAsync();
        return id;
    }

    private static Plan MakePlan(Guid ownerId, string visibility) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Access Plan",
        City = "Miami",
        Type = "personal",
        Visibility = visibility,
        CreatedById = ownerId,
    };

    // ── Owner ──
    [Fact]
    public async Task Owner_OnPrivatePlan_HasFullAccess()
    {
        var owner = await SeedUser();
        var plan = MakePlan(owner, "private");
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var access = await Resolve(plan.Id, owner);

        Assert.True(access.PlanExists);
        Assert.True(access.CanView);
        Assert.True(access.CanEdit);
        Assert.True(access.IsOwner);
        Assert.Equal("owner", access.Role);
    }

    // ── Collaborator editor ──
    [Fact]
    public async Task CollaboratorEditor_CanViewAndEdit_NotOwner()
    {
        var owner = await SeedUser();
        var editor = await SeedUser();
        var plan = MakePlan(owner, "private");
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        db.PlanCollaborators.Add(new PlanCollaborator { PlanId = plan.Id, UserId = editor, Role = "editor", InvitedBy = owner });
        await db.SaveChangesAsync();

        var access = await Resolve(plan.Id, editor);

        Assert.True(access.CanView);
        Assert.True(access.CanEdit);
        Assert.False(access.IsOwner);
        Assert.Equal("editor", access.Role);
    }

    // ── Collaborator viewer ──
    [Fact]
    public async Task CollaboratorViewer_CanViewButNotEdit()
    {
        var owner = await SeedUser();
        var viewer = await SeedUser();
        var plan = MakePlan(owner, "private");
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        db.PlanCollaborators.Add(new PlanCollaborator { PlanId = plan.Id, UserId = viewer, Role = "viewer", InvitedBy = owner });
        await db.SaveChangesAsync();

        var access = await Resolve(plan.Id, viewer);

        Assert.True(access.CanView);
        Assert.False(access.CanEdit);
        Assert.False(access.IsOwner);
        Assert.Equal("viewer", access.Role);
    }

    // ── Anonimo sobre plan publico ──
    [Fact]
    public async Task Anonymous_OnPublicPlan_CanViewOnly()
    {
        var owner = await SeedUser();
        var plan = MakePlan(owner, "public");
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var access = await Resolve(plan.Id, userId: null);

        Assert.True(access.PlanExists);
        Assert.True(access.CanView);
        Assert.False(access.CanEdit);
        Assert.False(access.IsOwner);
        Assert.Null(access.Role);
    }

    // ── Anonimo sobre plan privado ──
    [Fact]
    public async Task Anonymous_OnPrivatePlan_Denied()
    {
        var owner = await SeedUser();
        var plan = MakePlan(owner, "private");
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var access = await Resolve(plan.Id, userId: null);

        Assert.True(access.PlanExists);
        Assert.False(access.CanView);
        Assert.False(access.CanEdit);
    }

    // ── unlisted NO se resuelve por GUID por este metodo (ni anonimo ni tercero) ──
    [Fact]
    public async Task Unlisted_NotResolvedByGuid_ForAnonymousOrStranger()
    {
        var owner = await SeedUser();
        var stranger = await SeedUser();
        var plan = MakePlan(owner, "unlisted");
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var anon = await Resolve(plan.Id, userId: null);
        var third = await Resolve(plan.Id, stranger);

        Assert.False(anon.CanView);
        Assert.False(third.CanView);
        // El owner SIGUE viendo su plan unlisted.
        var ownerAccess = await Resolve(plan.Id, owner);
        Assert.True(ownerAccess.CanView);
        Assert.True(ownerAccess.IsOwner);
    }

    // ── Usuario ajeno sobre plan privado ──
    [Fact]
    public async Task Stranger_OnPrivatePlan_Denied()
    {
        var owner = await SeedUser();
        var stranger = await SeedUser();
        var plan = MakePlan(owner, "private");
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        await db.SaveChangesAsync();

        var access = await Resolve(plan.Id, stranger);

        Assert.True(access.PlanExists);
        Assert.False(access.CanView);
        Assert.False(access.CanEdit);
        Assert.False(access.IsOwner);
    }

    // ── Bloqueo owner->visor: niega vista de un plan PUBLICO ──
    [Fact]
    public async Task BlockedUser_CannotViewOwnersPublicPlan_OwnerBlockedViewer()
    {
        var owner = await SeedUser();
        var viewer = await SeedUser();
        var plan = MakePlan(owner, "public");
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        db.UserBlocks.Add(new UserBlock { BlockerId = owner, BlockedId = viewer });
        await db.SaveChangesAsync();

        var access = await Resolve(plan.Id, viewer);

        Assert.True(access.PlanExists);
        Assert.False(access.CanView);
        Assert.False(access.CanEdit);
    }

    // ── Bloqueo en direccion inversa (visor bloqueo al owner): tambien niega ──
    [Fact]
    public async Task BlockedUser_CannotViewOwnersPublicPlan_ViewerBlockedOwner()
    {
        var owner = await SeedUser();
        var viewer = await SeedUser();
        var plan = MakePlan(owner, "public");
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        db.UserBlocks.Add(new UserBlock { BlockerId = viewer, BlockedId = owner });
        await db.SaveChangesAsync();

        var access = await Resolve(plan.Id, viewer);

        Assert.False(access.CanView);
    }

    // ── El bloqueo NO afecta al propio owner viendo su plan ──
    [Fact]
    public async Task Owner_WithBlockAgainstThirdParty_StillSeesOwnPlan()
    {
        var owner = await SeedUser();
        var other = await SeedUser();
        var plan = MakePlan(owner, "public");
        var db = fixture.GetDbContext();
        db.Plans.Add(plan);
        db.UserBlocks.Add(new UserBlock { BlockerId = owner, BlockedId = other });
        await db.SaveChangesAsync();

        var access = await Resolve(plan.Id, owner);

        Assert.True(access.CanView);
        Assert.True(access.CanEdit);
        Assert.True(access.IsOwner);
    }

    // ── Plan inexistente ──
    [Fact]
    public async Task NonExistentPlan_ReturnsNotFound()
    {
        var user = await SeedUser();
        var access = await Resolve(Guid.NewGuid(), user);

        Assert.False(access.PlanExists);
        Assert.False(access.CanView);
        Assert.False(access.CanEdit);
        Assert.False(access.IsOwner);
        Assert.Null(access.Role);
    }
}
