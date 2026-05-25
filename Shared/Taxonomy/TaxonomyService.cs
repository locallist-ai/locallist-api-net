using LocalList.API.NET.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LocalList.API.NET.Shared.Taxonomy;

public class TaxonomyService : ITaxonomyService
{
    private const string CacheKey = "taxonomy:all";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly LocalListDbContext _db;
    private readonly IMemoryCache _cache;

    public TaxonomyService(LocalListDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<IReadOnlyList<SubcategoryDto>> GetAllAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyList<SubcategoryDto>? cached) && cached is not null)
            return cached;

        var all = await _db.Subcategories
            .AsNoTracking()
            .OrderBy(s => s.CategoryKey)
            .ThenBy(s => s.Key)
            .Select(s => new SubcategoryDto(s.Id, s.CategoryKey, s.Key, s.LabelEn, s.LabelEs, s.CreatedAt, s.UpdatedAt))
            .ToListAsync(ct);

        _cache.Set(CacheKey, (IReadOnlyList<SubcategoryDto>)all, CacheTtl);
        return all;
    }

    public async Task<IReadOnlyList<SubcategoryDto>> GetByCategoryAsync(string categoryKey, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all
            .Where(s => string.Equals(s.CategoryKey, categoryKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<bool> IsValidSubcategoryAsync(string categoryKey, string? subcategory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subcategory)) return true;
        var subs = await GetByCategoryAsync(categoryKey, ct);
        return subs.Any(s => string.Equals(s.Key, subcategory, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(s.LabelEn, subcategory, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DateTimeOffset> GetLastUpdatedAsync(CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.Count == 0 ? DateTimeOffset.UtcNow : all.Max(s => s.UpdatedAt);
    }

    public void Invalidate() => _cache.Remove(CacheKey);
}
