using LocalList.API.NET.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.NET.Shared.Access;

/// <summary>
/// Implementacion de <see cref="IPlanAccessService"/> sobre la BD real.
///
/// Reglas (en orden de precedencia):
///  1. Plan inexistente -> NotFound.
///  2. Owner (plans.created_by == userId) -> CanView + CanEdit + IsOwner. El owner SIEMPRE ve y
///     edita su plan; ningun bloqueo ni visibilidad se lo quita (no se afloja ownership existente).
///  3. Bloqueo entre userId y el owner (en cualquier direccion) -> Denied. Un usuario bloqueado no
///     ve el plan aunque sea publico. No aplica al propio owner (paso 2 ya retorno).
///  4. Colaborador role='editor' -> CanView + CanEdit. role='viewer' -> CanView.
///  5. visibility=='public' -> CanView (cualquiera, incluido anonimo userId==null).
///  6. visibility=='unlisted' o 'private' sin otra via -> Denied. 'unlisted' NO se resuelve por
///     GUID por este metodo: solo por token (S1).
/// </summary>
public class PlanAccessService : IPlanAccessService
{
    private readonly LocalListDbContext _db;

    public PlanAccessService(LocalListDbContext db)
    {
        _db = db;
    }

    public async Task<PlanAccess> GetAccessAsync(Guid planId, Guid? userId, CancellationToken ct)
    {
        var plan = await _db.Plans.AsNoTracking()
            .Where(p => p.Id == planId)
            .Select(p => new { p.CreatedById, p.Visibility })
            .FirstOrDefaultAsync(ct);

        if (plan == null)
            return PlanAccess.NotFound;

        var ownerId = plan.CreatedById;

        // 2. Owner: acceso total. Se evalua ANTES que bloqueo/visibilidad para no aflojar ownership.
        if (userId.HasValue && ownerId.HasValue && ownerId.Value == userId.Value)
            return new PlanAccess(true, CanView: true, CanEdit: true, IsOwner: true, Role: "owner");

        // 3. Bloqueo entre el visor y el owner (cualquier direccion) -> se niega todo.
        if (userId.HasValue && ownerId.HasValue)
        {
            var blocked = await _db.UserBlocks.AsNoTracking().AnyAsync(b =>
                (b.BlockerId == ownerId.Value && b.BlockedId == userId.Value) ||
                (b.BlockerId == userId.Value && b.BlockedId == ownerId.Value), ct);
            if (blocked)
                return PlanAccess.Denied;
        }

        // 4. Colaborador explicito.
        if (userId.HasValue)
        {
            var role = await _db.PlanCollaborators.AsNoTracking()
                .Where(c => c.PlanId == planId && c.UserId == userId.Value)
                .Select(c => c.Role)
                .FirstOrDefaultAsync(ct);

            if (role == "editor")
                return new PlanAccess(true, CanView: true, CanEdit: true, IsOwner: false, Role: "editor");
            if (role == "viewer")
                return new PlanAccess(true, CanView: true, CanEdit: false, IsOwner: false, Role: "viewer");
        }

        // 5. Visibilidad publica -> cualquiera puede ver (incluido anonimo).
        if (plan.Visibility == "public")
            return new PlanAccess(true, CanView: true, CanEdit: false, IsOwner: false, Role: null);

        // 6. Resto (private / unlisted sin via) -> denegado.
        return PlanAccess.Denied;
    }
}
