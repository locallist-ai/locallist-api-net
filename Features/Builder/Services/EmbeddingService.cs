using System.Text;
using System.Text.Json;
using Pgvector;

namespace LocalList.API.NET.Features.Builder.Services;

public class EmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<EmbeddingService> _logger;

    public const int Dimensions = 768;
    // text-embedding-004 se retiró 2026-01-14. gemini-embedding-001 es el reemplazo vigente.
    // Por defecto devuelve 3072 dims (Matryoshka); truncamos a 768 vía outputDimensionality
    // para preservar la columna vector(768) + índice HNSW ya existente.
    private const string DefaultModel = "gemini-embedding-001";
    private const int BatchMax = 100;

    public EmbeddingService(HttpClient httpClient, IConfiguration config, ILogger<EmbeddingService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<Vector?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vectors = await EmbedBatchAsync(new[] { text }, ct);
        return vectors.FirstOrDefault();
    }

    public async Task<IReadOnlyList<Vector>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return Array.Empty<Vector>();

        var apiKey = _config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Gemini API Key missing. Cannot produce embeddings.");
            return Array.Empty<Vector>();
        }

        var model = _config["Gemini:EmbeddingModel"];
        if (string.IsNullOrEmpty(model)) model = DefaultModel;

        var results = new List<Vector>(texts.Count);
        for (var i = 0; i < texts.Count; i += BatchMax)
        {
            var chunk = texts.Skip(i).Take(BatchMax).ToList();
            var vectors = await CallBatchEmbedAsync(chunk, model, apiKey, ct);
            results.AddRange(vectors);
        }
        return results;
    }

    private async Task<IReadOnlyList<Vector>> CallBatchEmbedAsync(
        IReadOnlyList<string> texts, string model, string apiKey, CancellationToken ct)
    {
        var requestBody = new
        {
            requests = texts.Select(t => new
            {
                model = $"models/{model}",
                content = new { parts = new[] { new { text = Sanitize(t) } } },
                outputDimensionality = Dimensions
            }).ToArray()
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:batchEmbedContents";
        var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("x-goog-api-key", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Gemini batchEmbedContents request failed");
            return Array.Empty<Vector>();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Polly/resilience timeout, no cancelación de cliente → degradación grácil.
            _logger.LogError("Gemini batchEmbedContents timed out");
            return Array.Empty<Vector>();
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("embeddings", out var embeddings))
            {
                _logger.LogError("Gemini response missing 'embeddings' property: {Body}", body);
                return Array.Empty<Vector>();
            }

            var vectors = new List<Vector>(embeddings.GetArrayLength());
            foreach (var e in embeddings.EnumerateArray())
            {
                if (!e.TryGetProperty("values", out var values)) continue;
                var arr = new float[values.GetArrayLength()];
                var idx = 0;
                foreach (var v in values.EnumerateArray()) arr[idx++] = v.GetSingle();
                if (arr.Length != Dimensions)
                {
                    _logger.LogError("Unexpected embedding dimensions: got {Got}, expected {Expected}", arr.Length, Dimensions);
                    continue;
                }
                // gemini-embedding-001 devuelve vectores sin normalizar cuando outputDimensionality < 3072
                // (Matryoshka truncation). Renormalizamos a L2=1 para que CosineDistance de pgvector se
                // comporte como similitud coseno pura.
                var norm = 0f;
                for (var k = 0; k < arr.Length; k++) norm += arr[k] * arr[k];
                norm = MathF.Sqrt(norm);
                if (norm > 0f)
                {
                    for (var k = 0; k < arr.Length; k++) arr[k] /= norm;
                }
                vectors.Add(new Vector(arr));
            }
            return vectors;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Gemini embed response");
            return Array.Empty<Vector>();
        }
    }

    private static string Sanitize(string input)
    {
        var s = input.Replace("\"", "'").Replace("\\", "");
        if (s.Length > 8000) s = s[..8000];
        return s;
    }

    public static string BuildPlaceIndexText(
        string name, string? category, string? neighborhood, string? city,
        string? whyThisPlace, IEnumerable<string>? bestFor, IEnumerable<string>? suitableFor)
    {
        var parts = new List<string> { name };
        if (!string.IsNullOrWhiteSpace(city)) parts.Add(city);
        if (!string.IsNullOrWhiteSpace(neighborhood)) parts.Add(neighborhood);
        if (!string.IsNullOrWhiteSpace(category)) parts.Add(category);
        if (!string.IsNullOrWhiteSpace(whyThisPlace)) parts.Add(whyThisPlace);
        if (bestFor != null) parts.Add(string.Join(" ", bestFor));
        if (suitableFor != null) parts.Add(string.Join(" ", suitableFor));
        return string.Join(". ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
