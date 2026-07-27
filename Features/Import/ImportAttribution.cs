using System.Text.RegularExpressions;

namespace LocalList.API.NET.Features.Import;

/// <summary>
/// Helpers de atribución de import (plataforma + handle del creador) compartidos por T1
/// (<c>POST /import/video</c>) y T4 (<c>POST /import/plan</c>). Antes cada controller tenía su copia
/// con semánticas DISTINTAS: T1 saneaba el handle como texto libre (conservaba el <c>@</c>) y por
/// defecto un request sin <c>platform</c> caía en <c>"other"</c> (→ 403 con el flag de terceros
/// apagado, contradiciendo el default documentado <c>self</c>); T4 usaba una regex estricta y el
/// default <c>self</c>. Unificado en la semántica ESTRICTA de T4 — el handle acaba pintado como
/// atribución en un plan, así que un formato acotado y predecible es lo correcto, y ambos endpoints
/// tratan la ausencia de plataforma como contenido PROPIO.
/// </summary>
public static class ImportAttribution
{
    /// <summary>Longitud máxima de <c>creatorHandle</c> tras sanear.</summary>
    public const int MaxCreatorHandleLength = 64;

    private static readonly string[] AllowedPlatforms = { "self", "tiktok", "instagram", "other" };

    /// <summary>Regex conservadora tipo handle: '@' opcional + 1..63 chars de [A-Za-z0-9_.-]. Se guarda sin '@'.</summary>
    private static readonly Regex HandlePattern = new(@"^@?[A-Za-z0-9_.\-]{1,63}$", RegexOptions.Compiled);

    /// <summary>
    /// Normaliza la plataforma a {self,tiktok,instagram,other}. Ausente/vacío → <c>"self"</c>
    /// (default documentado: v1 = contenido PROPIO); valor desconocido → <c>"other"</c>.
    /// </summary>
    public static string NormalizePlatform(string? raw)
    {
        var p = (raw ?? "self").Trim().ToLowerInvariant();
        if (p.Length == 0) return "self";
        return AllowedPlatforms.Contains(p) ? p : "other";
    }

    /// <summary>
    /// Valida contra <see cref="HandlePattern"/> y guarda sin '@'. Inválido/ausente → null (nunca
    /// se persiste sucio ni tumba la creación: la atribución es cosmética).
    /// </summary>
    public static string? SanitizeCreatorHandle(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (!HandlePattern.IsMatch(s)) return null;
        s = s.TrimStart('@');
        if (s.Length == 0 || s.Length > MaxCreatorHandleLength) return null;
        return s;
    }
}
