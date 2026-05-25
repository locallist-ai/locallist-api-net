using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Taxonomy;

namespace LocalList.API.NET.Features.Admin.Subcategories;

[ApiController]
[Route("admin/subcategories")]
[AdminAuthorize]
[EnableRateLimiting("AdminLimit")]
public class AdminSubcategoriesController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ITaxonomyService _taxonomy;
    private readonly TimeProvider _clock;
    private readonly ILogger<AdminSubcategoriesController> _logger;

    public AdminSubcategoriesController(
        LocalListDbContext db,
        ITaxonomyService taxonomy,
        TimeProvider clock,
        ILogger<AdminSubcategoriesController> logger)
    {
        _db = db;
        _taxonomy = taxonomy;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetSubcategories(
        [FromQuery] string? category,
        [FromQuery] bool includeDeleted = false,
        CancellationToken ct = default)
    {
        IQueryable<Subcategory> query = includeDeleted
            ? _db.Subcategories.IgnoreQueryFilters()
            : _db.Subcategories;

        if (!string.IsNullOrEmpty(category))
            query = query.Where(s => s.CategoryKey == category);

        var subs = await query
            .AsNoTracking()
            .OrderBy(s => s.CategoryKey)
            .ThenBy(s => s.Key)
            .ToListAsync(ct);

        return Ok(subs.Select(s => new SubcategoryDto(
            s.Id, s.CategoryKey, s.Key, s.LabelEn, s.LabelEs, s.CreatedAt, s.UpdatedAt)));
    }

    [HttpPost]
    public async Task<IActionResult> CreateSubcategory(
        [FromBody] CreateSubcategoryRequest request, CancellationToken ct)
    {
        // Check for duplicate (CategoryKey, Key) among active entries
        var exists = await _db.Subcategories
            .AnyAsync(s => s.CategoryKey == request.CategoryKey && s.Key == request.Key, ct);

        if (exists)
            return Conflict(new { error = $"Subcategory '{request.Key}' already exists for category '{request.CategoryKey}'.", code = "duplicate_subcategory" });

        var adminUserId = await User.GetUserIdAsync(_db, ct);
        var now = _clock.GetUtcNow();

        var sub = new Subcategory
        {
            Id = Guid.NewGuid(),
            CategoryKey = request.CategoryKey,
            Key = request.Key,
            LabelEn = request.LabelEn.Trim(),
            LabelEs = request.LabelEs.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByAdminUserId = adminUserId
        };

        _db.Subcategories.Add(sub);
        await _db.SaveChangesAsync(ct);

        _taxonomy.Invalidate();

        _logger.LogInformation("Admin created subcategory {Key} under {Category}", sub.Key, sub.CategoryKey);

        return CreatedAtAction(nameof(GetSubcategories), null,
            new SubcategoryDto(sub.Id, sub.CategoryKey, sub.Key, sub.LabelEn, sub.LabelEs, sub.CreatedAt, sub.UpdatedAt));
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> UpdateSubcategory(
        Guid id, [FromBody] UpdateSubcategoryRequest request, CancellationToken ct)
    {
        var sub = await _db.Subcategories.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sub == null) return NotFound(new { error = "Subcategory not found." });

        if (request.LabelEn is not null) sub.LabelEn = request.LabelEn.Trim();
        if (request.LabelEs is not null) sub.LabelEs = request.LabelEs.Trim();
        sub.UpdatedAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(ct);
        _taxonomy.Invalidate();

        _logger.LogInformation("Admin updated subcategory {Id} ({Key})", sub.Id, sub.Key);

        return Ok(new SubcategoryDto(sub.Id, sub.CategoryKey, sub.Key, sub.LabelEn, sub.LabelEs, sub.CreatedAt, sub.UpdatedAt));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteSubcategory(Guid id, CancellationToken ct)
    {
        var sub = await _db.Subcategories.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sub == null) return NotFound(new { error = "Subcategory not found." });

        sub.DeletedAt = _clock.GetUtcNow();
        sub.UpdatedAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(ct);
        _taxonomy.Invalidate();

        _logger.LogInformation("Admin soft-deleted subcategory {Id} ({Key})", sub.Id, sub.Key);

        return NoContent();
    }
}
