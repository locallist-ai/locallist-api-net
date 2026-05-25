namespace LocalList.API.NET.Shared.Taxonomy;

public interface ITaxonomyService
{
    Task<IReadOnlyList<SubcategoryDto>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SubcategoryDto>> GetByCategoryAsync(string categoryKey, CancellationToken ct = default);
    Task<bool> IsValidSubcategoryAsync(string categoryKey, string? subcategory, CancellationToken ct = default);
    Task<DateTimeOffset> GetLastUpdatedAsync(CancellationToken ct = default);
    void Invalidate();
}

public record SubcategoryDto(
    Guid Id,
    string CategoryKey,
    string Key,
    string LabelEn,
    string LabelEs,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);
