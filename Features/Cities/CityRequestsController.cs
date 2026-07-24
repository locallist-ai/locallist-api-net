using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Cities;

/// <summary>
/// Peticiones de cobertura de ciudad — Pablo 2026-07-25. Bajo el "¿No ves tu
/// ciudad?" del selector, la app expone un input libre para que el cliente nos
/// diga QUÉ ciudad quiere. Este endpoint recibe ese texto y lo guarda como
/// feedback (tabla <c>city_requests</c>).
///
/// Seguridad (el texto es un dato INERTE — nunca se interpreta ni renderiza):
///   - máx 100 caracteres (defensa de tamaño, además del cap global de Kestrel).
///   - validación de DOMINIO: solo caracteres de nombre de ciudad (letra unicode
///     inicial + letras/espacios/apóstrofe/guion/punto). Deja fuera del dominio
///     &lt;script&gt;, urls (javascript:...), emojis y payloads raros → la XSS real
///     se corta en la entrada; el output encoding de la futura admin es la 2ª capa.
///   - [AllowAnonymous] + rate limit dedicado (CityRequestLimit, 5/60s por IP) +
///     dedup suave 24h por (ip_hash, normalized_city) contra spam de repetición.
/// </summary>
[ApiController]
[Route("cities")]
[AllowAnonymous]
[EnableRateLimiting("CityRequestLimit")]
public partial class CityRequestsController : ControllerBase
{
    /// <summary>Longitud máxima del texto libre (requisito del fundador).</summary>
    private const int MaxCityLength = 100;

    /// <summary>Ventana de dedup suave: misma IP + misma ciudad no crea fila nueva.</summary>
    private static readonly TimeSpan DedupWindow = TimeSpan.FromHours(24);

    // Nombre de ciudad: letra unicode inicial + letras/espacios/apóstrofe/guion/punto.
    // Espejo conceptual de la regex del builder custom de la app
    // (^[\p{L}][\p{L}\s'\-.]*$), con \p{Zs} en vez de \s: solo espacios
    // HORIZONTALES unicode. \n/\t/\r internos → 400 city_invalid (rechazo, no
    // colapso) — city_text nunca guarda filas multilínea. Rechaza también
    // <script>, urls, emojis y basura.
    // Deuda conocida (review 2026-07-25, ACEPTADA): homóglifos cirílicos pasan
    // como \p{L} sin confusable-folding ("Мoscow" ≠ "Moscow" en normalized_city).
    // Revisar si el "top ciudades pedidas" se vuelve serio.
    [GeneratedRegex(@"^[\p{L}][\p{L}\p{Zs}'\-.]*$")]
    private static partial Regex CityNameRegex();

    private readonly LocalListDbContext _db;
    private readonly ILogger<CityRequestsController> _logger;
    private readonly IConfiguration _configuration;

    public CityRequestsController(
        LocalListDbContext db,
        ILogger<CityRequestsController> logger,
        IConfiguration configuration)
    {
        _db = db;
        _logger = logger;
        _configuration = configuration;
    }

    [HttpPost("request")]
    public async Task<IActionResult> RequestCity([FromBody] CityRequestDto request, CancellationToken ct)
    {
        var raw = request?.City?.Trim() ?? string.Empty;

        if (raw.Length == 0)
            return BadRequest(new { error = "city_required" });

        if (raw.Length > MaxCityLength)
            return BadRequest(new { error = "city_too_long" });

        if (!CityNameRegex().IsMatch(raw))
            return BadRequest(new { error = "city_invalid" });

        var normalized = CityNameNormalizer.Normalize(raw);
        if (normalized.Length == 0)
            return BadRequest(new { error = "city_invalid" });
        if (normalized.Length > 120) normalized = normalized[..120];

        var ipRaw = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ipHash = HashIp(ipRaw);

        // Dedup suave: la misma IP pidiendo la misma ciudad en 24h no crea fila
        // nueva (respuesta idempotente 200). No aplica a ip_hash vacío (sin IP
        // resoluble) para no colapsar peticiones distintas en un único bucket.
        if (!string.IsNullOrEmpty(ipHash))
        {
            var cutoff = DateTimeOffset.UtcNow - DedupWindow;
            var alreadyRequested = await _db.CityRequests.AnyAsync(
                cr => cr.IpHash == ipHash
                      && cr.NormalizedCity == normalized
                      && cr.CreatedAt >= cutoff,
                ct);
            if (alreadyRequested)
                return Ok(new { message = "Thanks, we've noted your city request." });
        }

        Guid? userId = null;
        if (User.Identity?.IsAuthenticated == true)
            userId = await User.GetUserIdAsync(_db, ct);

        var userAgent = Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 256) userAgent = userAgent[..256];

        var locale = Request.Headers.AcceptLanguage.ToString();
        if (locale.Length > 16) locale = locale[..16];

        _db.CityRequests.Add(new CityRequest
        {
            CityText = raw,
            NormalizedCity = normalized,
            UserId = userId,
            IpHash = string.IsNullOrEmpty(ipHash) ? null : ipHash,
            UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent,
            Locale = string.IsNullOrEmpty(locale) ? null : locale,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("City request stored ({Normalized})", normalized);

        return StatusCode(201, new { message = "Thanks, we've noted your city request." });
    }

    private string HashIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return string.Empty;
        var salt = _configuration["WAITLIST_IP_SALT"] ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes($"{salt}:{ip}");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public class CityRequestDto
{
    // Sin atributos de validación: la validación es defensiva en el controller
    // (trim + longitud + regex de dominio) y devuelve errores estructurados
    // (city_required / city_too_long / city_invalid), no el 400 de ModelState.
    public string? City { get; set; }
}
