namespace LocalList.API.NET.Shared.Usage;

/// <summary>
/// Consumo atómico de permisos por (user, feature, periodo) sobre la tabla
/// <c>usage_counters</c>. Contrato cross-slice: lo usan los gates de generación
/// (Chat + Builder) vía <see cref="IPlanGenerationGateService"/>.
/// </summary>
public interface IUsageCounterService
{
    /// <summary>
    /// Intenta consumir 1 permiso del contador (user, feature, periodStart) con techo
    /// <paramref name="limit"/>. Atómico frente a requests concurrentes: el increment es un
    /// upsert condicional en un solo statement SQL (el row-lock de Postgres serializa los
    /// increments y la condición <c>count &lt; limit</c> se re-evalúa sobre el valor
    /// commiteado), de modo que dos requests simultáneas NUNCA pueden gastar el mismo
    /// permiso dos veces ni superar el techo.
    /// </summary>
    /// <returns><c>true</c> si el permiso se consumió; <c>false</c> si el techo ya estaba alcanzado.</returns>
    Task<bool> TryConsumeAsync(Guid userId, string feature, DateOnly periodStart, int limit, CancellationToken ct);

    /// <summary>Lectura del consumo actual (0 si no hay fila). Solo para reporting en errores estructurados.</summary>
    Task<int> GetUsedAsync(Guid userId, string feature, DateOnly periodStart, CancellationToken ct);
}
