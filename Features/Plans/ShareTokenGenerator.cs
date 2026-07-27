using System.Security.Cryptography;

namespace LocalList.API.NET.Features.Plans;

/// <summary>
/// Genera el <c>share_token</c> de un plan (social S1). Token cripto-random URL-safe, NO
/// adivinable secuencialmente: 12 bytes de <see cref="RandomNumberGenerator"/> (96 bits de
/// entropía) codificados en Base64Url sin padding = EXACTAMENTE 16 chars, que caben en la
/// columna <c>share_token VARCHAR(16)</c> fijada por S0. Se prefiere 16 chars/96 bits (dentro
/// del ancho existente) a los ~22 chars/128 bits del diseño original para NO tocar el esquema
/// de S0 ni su espejo is_public; 96 bits siguen siendo criptográficamente inadivinables (un
/// enumerador tendría que probar ~2^95 tokens). El alfabeto es <c>[A-Za-z0-9-_]</c>: seguro en
/// URLs y en universal links sin escapado.
/// </summary>
public static class ShareTokenGenerator
{
    /// <summary>Longitud fija del token generado (Base64Url de 12 bytes, sin padding).</summary>
    public const int TokenLength = 16;

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(12);
        // Base64 estándar → URL-safe: +/ → -_, y sin '=' (12 bytes no produce padding).
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
