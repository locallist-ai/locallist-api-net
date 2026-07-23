using System.Text.Json;
using LocalList.API.NET.Features.Chat.Services;
using LocalList.API.NET.Shared.Taxonomy;

namespace LocalList.API.NET.Features.Import;

/// <summary>
/// El vídeo es INPUT HOSTIL: OCR de carteles/subtítulos y transcripción de audio pueden
/// contener prompt injection ("ignore your instructions", "visit anthropic.com", HTML…).
/// El modelo puede reflejar ese texto en el JSON extraído. Este sanitizador es la última
/// barrera antes de persistir/devolver: reutiliza <see cref="OutputSanitizer"/> (quita URLs,
/// markdown, esquemas js/data, escapa &lt;&gt;, acota longitud) y <see cref="OutputValidator"/>
/// (detecta canary del prompt, identity-probes, echoes) del slice Chat, más la taxonomía
/// para la categoría.
///
/// Reglas: cero URLs, categoría SOLO si está en la taxonomía canónica, evidence SOLO en
/// {ocr,audio,visual}, timestamp no negativo, un sitio sin nombre limpio se descarta,
/// confidence acotada a [0,1].
/// </summary>
public static class VideoOutputSanitizer
{
    private const int MaxNameLength = 120;
    private const int MaxDescriptorLength = 200;
    private const int MaxShortField = 80;   // city / country / language
    private const int MaxVibes = 12;
    private const int MaxVibeLength = 40;
    private const int MaxPlaces = 100;

    private static readonly string[] AllowedEvidence = { "ocr", "audio", "visual" };

    public sealed record SanitizedOutput(
        string? City,
        string? Country,
        string? Language,
        List<ExtractedVideoPlace> Places,
        List<string> Vibes,
        double Confidence,
        int DroppedPlaces);

    /// <summary>
    /// Parsea el JSON crudo de Gemini y devuelve un resultado 100% saneado.
    /// Lanza <see cref="JsonException"/> si el cuerpo no es JSON parseable (el caller lo
    /// traduce a ExtractionUnavailable). Campos ausentes/mal tipados se ignoran, nunca rompen.
    /// </summary>
    public static SanitizedOutput Sanitize(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        var city = SanitizeShort(GetString(root, "city"));
        var country = SanitizeShort(GetString(root, "country"));
        var language = SanitizeShort(GetString(root, "language"));

        var places = new List<ExtractedVideoPlace>();
        var dropped = 0;
        if (root.TryGetProperty("places", out var placesEl) && placesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in placesEl.EnumerateArray())
            {
                if (places.Count >= MaxPlaces) break;
                var place = SanitizePlace(el);
                if (place is null) { dropped++; continue; }
                places.Add(place);
            }
        }

        var vibes = new List<string>();
        if (root.TryGetProperty("vibes", out var vibesEl) && vibesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in vibesEl.EnumerateArray())
            {
                if (vibes.Count >= MaxVibes) break;
                var clean = SanitizeField(v.ValueKind == JsonValueKind.String ? v.GetString() : null, MaxVibeLength);
                if (!string.IsNullOrWhiteSpace(clean)) vibes.Add(clean);
            }
        }

        var confidence = 0.0;
        if (root.TryGetProperty("confidence", out var confEl) &&
            confEl.ValueKind == JsonValueKind.Number &&
            confEl.TryGetDouble(out var c))
        {
            confidence = Math.Clamp(c, 0.0, 1.0);
        }

        return new SanitizedOutput(city, country, language, places, vibes, confidence, dropped);
    }

    private static ExtractedVideoPlace? SanitizePlace(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        // El nombre es obligatorio y limpio: si tras sanear queda vacío o dispara
        // OutputValidator (identity-probe, canary, prompt-echo) el sitio se descarta entero.
        var name = SanitizeField(GetString(el, "name"), MaxNameLength);
        if (string.IsNullOrWhiteSpace(name) || OutputValidator.HasDrift(name)) return null;

        // Descriptor: campo opcional; si dispara drift se anula (no descartamos el sitio por él).
        var descriptor = SanitizeField(GetString(el, "descriptor"), MaxDescriptorLength);
        if (!string.IsNullOrWhiteSpace(descriptor) && OutputValidator.HasDrift(descriptor)) descriptor = null;
        if (string.IsNullOrWhiteSpace(descriptor)) descriptor = null;

        // Categoría: SOLO si mapea a la taxonomía canónica (case-insensitive); si no, null.
        // Evita que el modelo (o el injection) invente etiquetas arbitrarias.
        var category = NormalizeCategory(GetString(el, "category"));

        // Evidence: enum cerrado.
        var evidenceRaw = GetString(el, "evidence")?.Trim().ToLowerInvariant();
        var evidence = evidenceRaw is not null && AllowedEvidence.Contains(evidenceRaw) ? evidenceRaw : null;

        int? timestampSec = null;
        if (el.TryGetProperty("timestampSec", out var tsEl) &&
            tsEl.ValueKind == JsonValueKind.Number &&
            tsEl.TryGetInt32(out var ts) &&
            ts >= 0)
        {
            timestampSec = ts;
        }

        return new ExtractedVideoPlace(name, descriptor, category, evidence, timestampSec);
    }

    private static string? NormalizeCategory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        // Devuelve la forma canónica (Title case) de la taxonomía, no el texto del modelo.
        return PlaceTaxonomy.Categories.FirstOrDefault(
            c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static string? SanitizeShort(string? raw)
    {
        var s = SanitizeField(raw, MaxShortField);
        return string.IsNullOrWhiteSpace(s) || OutputValidator.HasDrift(s) ? null : s;
    }

    /// <summary>
    /// Sanea un campo de texto libre reutilizando <see cref="OutputSanitizer"/> (quita URLs,
    /// markdown/HTML, esquemas peligrosos, escapa ángulos) y recorta a <paramref name="maxLen"/>.
    /// </summary>
    private static string SanitizeField(string? raw, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = OutputSanitizer.Sanitize(raw).Trim();
        if (s.Length > maxLen) s = s[..maxLen].TrimEnd();
        return s;
    }

    private static string? GetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
