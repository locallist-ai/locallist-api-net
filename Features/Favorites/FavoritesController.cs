using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.I18n;

namespace LocalList.API.NET.Features.Favorites;

/// <summary>
/// Favoritos de sitios (F-BE del build-out post-1.0). v1 = solo <see cref="Place"/> (no planes).
/// Todos los endpoints exigen auth AppScheme: un invitado recibe 401 (la app lo mapea a
/// <c>signup_required</c>). El cap (50 free · ilimitado Plus) se lee con el tier FRESCO de DB
/// (patrón de #108: nunca del claim JWT, que vive 15 min y es forjable).
///
/// ATOMICIDAD DEL CAP (TOCTOU): favoritar es un "insert condicional a que count &lt; 50", y el
/// count abarca MUCHAS filas (una por place), así que —a diferencia de <c>usage_counters</c>— no
/// hay una única fila-contador cuyo row-lock sirva. Un <c>INSERT … SELECT WHERE (count) &lt; 50</c>
/// bajo READ COMMITTED puede sobrepasar: dos statements concurrentes evalúan el subquery sobre el
/// snapshot pre-insert (49) y ambos insertan → 51. Para cerrarlo serializamos por usuario con un
/// <c>pg_advisory_xact_lock</c> (se libera solo al COMMIT/ROLLBACK de la transacción): dentro del
/// lock el conteo es exacto y no hay overshoot. El 23505 del índice único (user_id, place_id)
/// queda como defensa en profundidad para la idempotencia.
/// </summary>
[ApiController]
[Route("favorites")]
[Authorize]
public class FavoritesController : ControllerBase
{
    /// <summary>Cap de favoritos para tier free. Plus/pro = ilimitado.</summary>
    public const int FreeFavoritesLimit = 50;

    private const string TierPro = "pro";

    private readonly LocalListDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IConfiguration _config;
    private readonly ILogger<FavoritesController> _logger;

    public FavoritesController(
        LocalListDbContext db,
        TimeProvider clock,
        IConfiguration config,
        ILogger<FavoritesController> logger)
    {
        _db = db;
        _clock = clock;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Favorita un place. Idempotente: si ya estaba favoritado devuelve 200 sin duplicar. Place
    /// inexistente o no publicado → 404 opaco (no confirma existencia de borradores). Si el tier
    /// es free y ya hay 50 favoritos de places PUBLICADOS (misma semántica que el GET: lo que
    /// ves = lo que cuenta) → 403 estructurado <c>favorites_limit_reached</c> (familia
    /// <c>*_limit_reached</c> que la app mapea a upsell). Cap comprobado atómicamente (ver clase).
    /// </summary>
    [HttpPut("{placeId:guid}")]
    public async Task<IActionResult> AddFavorite(Guid placeId, CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null) return Unauthorized(new { error = "Invalid token claims" });

        // Place debe existir Y estar publicado. Mismo 404 opaco para "no existe" y "no publicado":
        // no confirmamos la existencia de borradores/curación interna a un cliente de la app.
        var placePublished = await _db.Places
            .AnyAsync(p => p.Id == placeId && p.Status == "published", ct);
        if (!placePublished)
            return NotFound(new { error = "Place not found" });

        // Serializamos por usuario: dentro de esta transacción, el lock consultivo garantiza que
        // ninguna otra request del MISMO usuario pueda insertar entre el conteo y nuestro insert.
        // Colisión del hash de 64 bits (hashtextextended) entre DOS USUARIOS DISTINTOS
        // (~2^32 usuarios por birthday bound): benigna — solo serializaría cruzadamente sus
        // requests (una espera a la otra), jamás produce un conteo/cap incorrecto.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({userId.Value.ToString()}, {AdvisoryLockSeed}))", ct);

        // Tier FRESCO de DB (dentro del lock). tier null = identidad muerta (token válido de un
        // user ya borrado) → mismo 401 que el resto de gates, no un 403 de catálogo.
        var tier = await _db.Users
            .Where(u => u.Id == userId.Value)
            .Select(u => u.Tier)
            .FirstOrDefaultAsync(ct);
        if (tier is null)
        {
            await tx.RollbackAsync(ct);
            return Unauthorized(new { error = "Invalid token claims" });
        }

        // Ya favoritado → idempotente 200, sin tocar nada ni consumir cupo.
        var already = await _db.Favorites
            .AnyAsync(f => f.UserId == userId.Value && f.PlaceId == placeId, ct);
        if (already)
        {
            await tx.CommitAsync(ct);
            return Ok(new { favorited = true });
        }

        var isPro = string.Equals(tier, TierPro, StringComparison.Ordinal);
        if (!isPro)
        {
            // El cap cuenta SOLO favoritos de places PUBLICADOS — la MISMA semántica que el
            // GET (decisión hub post-review): lo que ves = lo que cuenta. Sin este filtro, un
            // free con places despublicados quedaba atascado (403 used:50 viendo total:40).
            // Borde aceptado: si places despublicados se REPUBLICAN y el usuario supera 50
            // visibles, no pasa nada — el siguiente PUT devuelve 403 hasta bajar de 50, y el
            // GET sigue mostrando todos los publicados (puede ser >50).
            var count = await _db.Favorites
                .Where(f => f.UserId == userId.Value)
                .Join(_db.Places.Where(p => p.Status == "published"),
                      f => f.PlaceId, p => p.Id, (f, _) => f)
                .CountAsync(ct);
            if (count >= FreeFavoritesLimit)
            {
                await tx.CommitAsync(ct);
                _logger.LogInformation("Favorites cap hit userId={UserId} count={Count}", userId.Value, count);
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "favorites_limit_reached",
                    used = count,
                    limit = FreeFavoritesLimit,
                });
            }
        }

        _db.Favorites.Add(new Favorite
        {
            UserId = userId.Value,
            PlaceId = placeId,
            CreatedAt = _clock.GetUtcNow(),
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Defensa en profundidad: aunque el lock serializa a este usuario, tragamos el 23505
            // del índice único (user_id, place_id) para que un favoritar duplicado siga siendo
            // idempotente en cualquier interleaving imprevisto.
            await tx.CommitAsync(ct);
            return Ok(new { favorited = true });
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex))
        {
            // Carrera con un hard-delete (admin) del place: el check de published pasó pero el
            // insert choca con el FK a places (23503) porque el place ya no existe. Mismo 404
            // opaco que si nunca hubiera existido — nunca un 500. La transacción no commiteada
            // se revierte al salir del using.
            return NotFound(new { error = "Place not found" });
        }

        await tx.CommitAsync(ct);
        return Ok(new { favorited = true });
    }

    /// <summary>
    /// Desfavorita un place. Idempotente: si no existía la fila devuelve 204 igual (no hay estado
    /// que revertir). No necesita el lock del cap — borrar nunca puede sobrepasar el límite.
    /// </summary>
    [HttpDelete("{placeId:guid}")]
    public async Task<IActionResult> RemoveFavorite(Guid placeId, CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null) return Unauthorized(new { error = "Invalid token claims" });

        await _db.Favorites
            .Where(f => f.UserId == userId.Value && f.PlaceId == placeId)
            .ExecuteDeleteAsync(ct);

        return NoContent();
    }

    /// <summary>
    /// Lista paginada de los places favoritos del usuario como <see cref="PlaceDto"/> (fotos
    /// sintetizadas por el proxy vía <c>Api:PublicBaseUrl</c>, nunca la key de Google). Orden:
    /// <c>created_at DESC</c> con tiebreaker <c>place_id DESC</c> — orden TOTAL para que Postgres
    /// no duplique/omita en fronteras de página (gotcha conocido del repo). Solo places publicados
    /// (coherente con la visibilidad pública del resto de la API); <c>total</c> refleja lo listado.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetFavorites(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null) return Unauthorized(new { error = "Invalid token claims" });

        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);

        var query =
            from f in _db.Favorites.AsNoTracking()
            join p in _db.Places.AsNoTracking() on f.PlaceId equals p.Id
            where f.UserId == userId.Value && p.Status == "published"
            orderby f.CreatedAt descending, p.Id descending
            select p;

        var total = await query.CountAsync(ct);

        var places = await query
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var lang = LanguageAccessor.ResolveRequestLanguage(Request);
        var publicBaseUrl = _config["Api:PublicBaseUrl"];
        var placeDtos = places.Select(p => PlaceDto.FromEntity(p, lang, publicBaseUrl)).ToList();

        return Ok(new { places = placeDtos, total });
    }

    /// <summary>
    /// Lista ligera de los <c>placeId</c> favoritos del usuario, para que la app pinte los
    /// corazones sin traer los <see cref="PlaceDto"/> completos. Vía única elegida (frente a
    /// meterlos en <c>GET /account</c>): mantiene <c>/account</c> acotado y este endpoint barato.
    /// Devuelve TODOS los ids (el cap de 50 mantiene el payload trivial); incluye favoritos de
    /// places que pudieran haberse despublicado, para que el estado del corazón sea consistente.
    /// </summary>
    [HttpGet("ids")]
    public async Task<IActionResult> GetFavoriteIds(CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null) return Unauthorized(new { error = "Invalid token claims" });

        var ids = await _db.Favorites.AsNoTracking()
            .Where(f => f.UserId == userId.Value)
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.PlaceId)
            .Select(f => f.PlaceId)
            .ToListAsync(ct);

        return Ok(new { ids });
    }

    /// <summary>Semilla del lock consultivo por usuario — namespacea el hash contra otros usos de advisory locks.</summary>
    private const long AdvisoryLockSeed = 0x_FA_00_71_7E; // "FA…RITE"

    /// <summary>Detecta la violación del índice único (user_id, place_id) en Postgres (SqlState "23505").</summary>
    internal static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation;

    /// <summary>
    /// Detecta la violación del FK a places en Postgres (SqlState "23503") — carrera con un
    /// hard-delete del place entre el check de published y el insert. Internal para el test
    /// unitario del catch (la carrera real no es reproducible determinísticamente vía ApiFixture:
    /// exigiría pausar la request entre el check y el SaveChanges).
    /// </summary>
    internal static bool IsForeignKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.ForeignKeyViolation;
}
