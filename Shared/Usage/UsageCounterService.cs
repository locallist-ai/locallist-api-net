using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Shared.Usage;

/// <summary>
/// Implementación sobre Postgres. El corazón es un <c>INSERT … ON CONFLICT … DO UPDATE …
/// WHERE count &lt; limit</c> en un único statement: si dos requests del mismo usuario
/// compiten, la segunda espera el row-lock de la primera y re-evalúa la condición sobre el
/// count ya commiteado — no hay ventana read-modify-write. <c>ExecuteSql</c> devuelve las
/// filas afectadas: 1 = permiso consumido (insert o update aplicado), 0 = el WHERE del
/// UPDATE no se cumplió (techo alcanzado).
/// </summary>
public class UsageCounterService : IUsageCounterService
{
    private readonly LocalListDbContext _db;

    public UsageCounterService(LocalListDbContext db) => _db = db;

    public async Task<bool> TryConsumeAsync(
        Guid userId, string feature, DateOnly periodStart, int limit, CancellationToken ct)
    {
        // Guard explícito: el camino INSERT del upsert no pasa por el WHERE del UPDATE,
        // así que con limit <= 0 un primer consumo colaría count=1 > limit.
        if (limit <= 0) return false;

        var affected = await _db.Database.ExecuteSqlAsync($"""
            INSERT INTO usage_counters AS uc (user_id, feature, period_start, count)
            VALUES ({userId}, {feature}, {periodStart}, 1)
            ON CONFLICT (user_id, feature, period_start)
            DO UPDATE SET count = uc.count + 1
            WHERE uc.count < {limit}
            """, ct);

        return affected == 1;
    }

    public Task<int> GetUsedAsync(Guid userId, string feature, DateOnly periodStart, CancellationToken ct) =>
        _db.UsageCounters
            .Where(uc => uc.UserId == userId && uc.Feature == feature && uc.PeriodStart == periodStart)
            .Select(uc => uc.Count)
            .FirstOrDefaultAsync(ct);
}
