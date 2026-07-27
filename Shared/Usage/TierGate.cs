using LocalList.API.NET.Shared.Constants;
using LocalList.API.NET.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.NET.Shared.Usage;

/// <summary>
/// Lectura del tier FRESCO de DB (nunca el claim del JWT, que vive 15 min y es forjable) — el
/// patrón que comparten los gates de Import (T1/T4), Favorites y la generación. Centralizado aquí
/// para no repetir la query <c>Users.Where(id).Select(Tier).FirstOrDefault</c> + la comparación con
/// <see cref="Tiers.Pro"/> en cada sitio. Cada gate conserva su propio mapeo de error (401 para
/// identidad muerta vs 403 para free), por eso el helper devuelve el tier crudo, no solo un bool.
/// </summary>
public static class TierGate
{
    /// <summary>
    /// Tier actual del usuario leído de DB. <c>null</c> = identidad muerta (token válido de un
    /// usuario ya borrado) → el caller lo mapea a 401, no a un 403 de catálogo.
    /// </summary>
    public static Task<string?> GetFreshTierAsync(LocalListDbContext db, Guid userId, CancellationToken ct)
        => db.Users.Where(u => u.Id == userId).Select(u => (string?)u.Tier).FirstOrDefaultAsync(ct);

    /// <summary>True si el tier corresponde a Plus/pro.</summary>
    public static bool IsPro(string? tier) => string.Equals(tier, Tiers.Pro, StringComparison.Ordinal);

    /// <summary>Conveniencia: lee el tier fresco y devuelve si es pro (gates que no distinguen null vs free).</summary>
    public static async Task<bool> IsProAsync(LocalListDbContext db, Guid userId, CancellationToken ct)
        => IsPro(await GetFreshTierAsync(db, userId, ct));
}
