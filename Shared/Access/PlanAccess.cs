namespace LocalList.API.NET.Shared.Access;

/// <summary>
/// Resultado de la resolucion de acceso a un plan para un usuario dado (o anonimo).
/// Punto UNICO consultado por PlansController.GetPlan, PlanEditController y FollowController.
/// </summary>
/// <param name="PlanExists">false si el plan no existe (el resto de flags son false).</param>
/// <param name="CanView">Puede leer el plan (owner, colaborador, o visibility='public').</param>
/// <param name="CanEdit">Puede mutar el plan (owner o colaborador con role='editor').</param>
/// <param name="IsOwner">Es el creador del plan (plans.created_by == userId).</param>
/// <param name="Role">'owner' | 'editor' | 'viewer' | null (acceso via visibility publica).</param>
public readonly record struct PlanAccess(
    bool PlanExists,
    bool CanView,
    bool CanEdit,
    bool IsOwner,
    string? Role)
{
    /// <summary>El plan no existe: acceso nulo.</summary>
    public static readonly PlanAccess NotFound = new(false, false, false, false, null);

    /// <summary>El plan existe pero el usuario no tiene ningun acceso.</summary>
    public static readonly PlanAccess Denied = new(true, false, false, false, null);
}
