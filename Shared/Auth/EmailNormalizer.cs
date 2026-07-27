namespace LocalList.API.NET.Shared.Auth;

/// <summary>
/// Punto ÚNICO de canonicalización de email para toda la superficie de auth.
/// Un email es la MISMA cuenta independientemente de la caja o los espacios de borde
/// (<c>Pablo@x.com</c> == <c> pablo@x.com </c>). Normalizar en TODA escritura y comparación
/// evita cuentas duplicadas por variante; el esquema lo refuerza con <c>citext</c> +
/// índice único case-insensitive sobre <c>users.email</c>.
///
/// Null/whitespace → string vacío: los callers ya validan presencia antes (DTO <c>[Required]</c>,
/// <c>string.IsNullOrEmpty(claims.Email)</c>, <c>IsNullOrWhiteSpace(request.Email)</c>), y devolver
/// "" mantiene ese guard funcionando (nunca lanza, nunca deja pasar un valor sucio).
/// </summary>
public static class EmailNormalizer
{
    public static string Normalize(string? email) =>
        email?.Trim().ToLowerInvariant() ?? string.Empty;
}
