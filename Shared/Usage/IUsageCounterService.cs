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

    /// <summary>
    /// Devuelve 1 permiso previamente consumido (decrement atómico, con suelo en 0). Usado
    /// cuando una operación consumió el slot pero acabó sin entregar valor y no debe cobrarlo:
    ///   - reembolso cruzado entre dos ventanas (se consumió la ventana A pero la B estaba al
    ///     tope → se devuelve A y se rechaza), y
    ///   - reembolso por fallo de infraestructura (p.ej. el import de vídeo consume cuota antes
    ///     de llamar a Gemini para acotar coste, y la devuelve si la extracción no es viable).
    /// Atómico como el consume: un único <c>UPDATE … SET count = count - 1 WHERE count &gt; 0</c>
    /// (el <c>count &gt; 0</c> evita bajar de cero y serializa con el row-lock de Postgres).
    /// No-op si no hay fila o el contador ya está en 0.
    /// </summary>
    Task ReleaseAsync(Guid userId, string feature, DateOnly periodStart, CancellationToken ct);

    /// <summary>Lectura del consumo actual (0 si no hay fila). Solo para reporting en errores estructurados.</summary>
    Task<int> GetUsedAsync(Guid userId, string feature, DateOnly periodStart, CancellationToken ct);
}
