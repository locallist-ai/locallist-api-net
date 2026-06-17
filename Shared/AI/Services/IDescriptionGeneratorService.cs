namespace LocalList.API.NET.Shared.AI.Services;

public interface IDescriptionGeneratorService
{
    Task<string?> GeneratePlaceDescriptionAsync(
        string name, string city, string category,
        string? subcategory, IEnumerable<string>? googleTypes,
        decimal? rating, int? reviewCount, string? neighborhood,
        CancellationToken ct = default);

    Task<GeneratePlaceDescriptionResult> GeneratePlaceDescriptionWithDiagnosticsAsync(
        string name, string city, string category,
        string? subcategory, IEnumerable<string>? googleTypes,
        decimal? rating, int? reviewCount, string? neighborhood,
        CancellationToken ct = default);
}
