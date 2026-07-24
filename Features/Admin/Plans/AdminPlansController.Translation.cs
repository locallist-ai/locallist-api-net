using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.I18n;

namespace LocalList.API.NET.Features.Admin.Plans;

// ES translation draft + batch translation for curated plans. Logic is identical to the
// original single-file version; only its location changed.
public partial class AdminPlansController
{
    [HttpPost("{id}/translate")]
    public async Task<IActionResult> TranslatePlan(Guid id, CancellationToken ct)
    {
        var plan = await _db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan == null) return NotFound(new { error = "Plan not found" });

        if (plan.Source != "curated")
            return BadRequest(new { error = "Translation is only supported for curated plans." });

        var draft = await _ai.TranslatePlanAsync(plan, "es", ct);
        if (draft == null)
            return StatusCode(503, new { error = "Translation service unavailable." });

        _logger.LogInformation("Translation: entity=Plan id={Id} lang=es action=draft", plan.Id);

        return Ok(new { nameEs = draft.Name, descriptionEs = draft.Description });
    }

    /// <summary>
    /// Backfill: translate all curated plans without ES translation.
    /// Idempotent — only processes plans missing name_i18n.es.
    /// </summary>
    [HttpPost("translate-batch")]
    public async Task<IActionResult> TranslateBatch([FromQuery] string lang = "es", [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        var allCurated = await _db.Plans
            .Where(p => p.Source == "curated")
            .ToListAsync(ct);

        var toTranslate = allCurated
            .Where(p => p.NameI18n == null
                     || !p.NameI18n.RootElement.TryGetProperty(lang, out _))
            .ToList();

        if (toTranslate.Count == 0)
            return Ok(new { translated = 0, failed = 0, skipped = allCurated.Count,
                remaining = 0,
                message = $"All curated plans already have '{lang}' translation." });

        var totalPending = toTranslate.Count;
        var batch = toTranslate.Take(limit).ToList();
        var translated = 0;
        var failed = 0;

        foreach (var chunk in batch.Chunk(5))
        {
            foreach (var plan in chunk)
            {
                if (ct.IsCancellationRequested) break;

                var draft = await _ai.TranslatePlanAsync(plan, lang, ct);
                if (draft == null) { failed++; continue; }

                plan.NameI18n = LanguageAccessor.SetI18nString(plan.NameI18n, lang, draft.Name);
                plan.DescriptionI18n = LanguageAccessor.SetI18nString(plan.DescriptionI18n, lang, draft.Description);
                plan.TranslationStatus = LanguageAccessor.SetI18nString(plan.TranslationStatus, lang, "approved");
                plan.UpdatedAt = _clock.GetUtcNow();

                _logger.LogInformation("Translation: entity=Plan id={Id} lang={Lang} action=approved", plan.Id, lang);
                translated++;
            }

            // Save progress after each chunk — allows partial resumption on timeout
            if (!ct.IsCancellationRequested)
                await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("translate-batch plans: translated={T} failed={F} skipped={S} remaining={R}",
            translated, failed, allCurated.Count - toTranslate.Count, totalPending - translated - failed);

        return Ok(new
        {
            translated,
            failed,
            skipped = allCurated.Count - toTranslate.Count,
            remaining = totalPending - translated - failed
        });
    }
}
