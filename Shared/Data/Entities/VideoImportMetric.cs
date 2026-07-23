using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

/// <summary>
/// Diagnóstico de un intento de extracción de vídeo (F2). Mismo espíritu que
/// <see cref="ChatTurn"/> / <see cref="PlanMetric"/>: tokens, coste, latencia y resultado
/// se persisten para observabilidad admin y control de coste. Tabla standalone (sin FK):
/// aún no hay un registro de import al que colgarse (el endpoint T1 llegará después).
///
/// IMPORTANTE (retención): NO se persiste el fichero, sus bytes, ni el file_uri de Gemini.
/// El vídeo se borra tras extraer; aquí solo quedan metadatos y diagnóstico.
/// </summary>
[Table("video_import_metrics")]
public class VideoImportMetric
{
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Plataforma de origen del vídeo (tiktok/instagram/…), tal cual la aporta el caller.</summary>
    [Column("platform")]
    public string? Platform { get; set; }

    [Column("model")]
    public string Model { get; set; } = string.Empty;

    [Column("ai_provider")]
    public string AiProvider { get; set; } = "gemini";

    [Column("mime_type")]
    public string? MimeType { get; set; }

    [Column("size_bytes")]
    public long? SizeBytes { get; set; }

    [Column("duration_sec")]
    public double? DurationSec { get; set; }

    /// <summary>¿El caller aportó caption como contexto extra?</summary>
    [Column("caption_provided")]
    public bool CaptionProvided { get; set; }

    [Column("city")]
    public string? City { get; set; }

    [Column("country")]
    public string? Country { get; set; }

    [Column("language")]
    public string? Language { get; set; }

    [Column("num_places")]
    public int NumPlaces { get; set; }

    /// <summary>Sitios descartados por el sanitizador (nombre vacío, drift/injection detectado).</summary>
    [Column("num_places_dropped")]
    public int NumPlacesDropped { get; set; }

    [Column("confidence")]
    public double? Confidence { get; set; }

    [Column("input_tokens")]
    public int? InputTokens { get; set; }

    [Column("output_tokens")]
    public int? OutputTokens { get; set; }

    [Column("thinking_tokens")]
    public int? ThinkingTokens { get; set; }

    [Column("total_tokens")]
    public int? TotalTokens { get; set; }

    /// <summary>Estimación a priori de tokens de media (258 tok/s vídeo + 32 tok/s audio). Ver VideoCostEstimator.</summary>
    [Column("estimated_media_tokens")]
    public int? EstimatedMediaTokens { get; set; }

    [Column("cost_usd")]
    public decimal? CostUsd { get; set; }

    [Column("latency_ms")]
    public int LatencyMs { get; set; }

    [Column("finish_reason")]
    public string? FinishReason { get; set; }

    /// <summary>Null en éxito; código estable (no_places_found, extraction_unavailable, video_too_long…) en fallo.</summary>
    [Column("error_code")]
    public string? ErrorCode { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }
}
