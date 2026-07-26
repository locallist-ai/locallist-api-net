namespace LocalList.API.NET.Shared.Access;

/// <summary>
/// Servicio central de autorizacion de planes. Sustituye los checks de ownership inline
/// duplicados en los controllers. Es el punto UNICO que S1+ y favoritos consultan: cualquier
/// cambio de semantica de acceso a planes vive aqui, no disperso por los controllers.
/// </summary>
public interface IPlanAccessService
{
    /// <summary>
    /// Resuelve el acceso de <paramref name="userId"/> (null = anonimo) al plan
    /// <paramref name="planId"/>. Nunca lanza por plan inexistente: devuelve
    /// <see cref="PlanAccess.NotFound"/>.
    /// </summary>
    Task<PlanAccess> GetAccessAsync(Guid planId, Guid? userId, CancellationToken ct);
}
