using System.Text.Json;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.AI.Security;
using LocalList.API.NET.Shared.AI.Services;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// The LocalList brand bans long dashes in user-visible copy; only the LLM (and Google
/// editorial summaries) introduce them, so <see cref="TypographySanitizer"/> normalizes
/// them to a plain hyphen-minus at every AI/external write choke point. These tests cover
/// the helper itself plus the two production seams that were previously NOT stripping.
/// </summary>
public class TypographySanitizerTests
{
    // ── Helper: every long-dash variant → hyphen-minus ────────────────────────
    [Theory]
    [InlineData("a‒b")] // figure dash ‒
    [InlineData("a–b")] // en dash –
    [InlineData("a—b")] // em dash —
    [InlineData("a―b")] // horizontal bar ―
    [InlineData("a−b")] // minus sign −
    public void StripLongDashes_EveryVariant_BecomesHyphenMinus(string input)
    {
        var result = TypographySanitizer.StripLongDashes(input);

        Assert.Equal("a-b", result);
        Assert.DoesNotContain('‒', result!);
        Assert.DoesNotContain('–', result!);
        Assert.DoesNotContain('—', result!);
        Assert.DoesNotContain('―', result!);
        Assert.DoesNotContain('−', result!);
    }

    // ── Helper: real sentence reads cleanly, no double space ──────────────────
    [Fact]
    public void StripLongDashes_Sentence_ReadsCleanly()
    {
        var result = TypographySanitizer.StripLongDashes("great food — perfect for families");

        Assert.Equal("great food - perfect for families", result);
        Assert.DoesNotContain('—', result!);
        Assert.DoesNotContain("  ", result); // no double space anywhere
    }

    // ── Helper: doubled spaces around the dash are collapsed ──────────────────
    [Fact]
    public void StripLongDashes_DoubledSpacesAroundDash_Collapsed()
    {
        var result = TypographySanitizer.StripLongDashes("great food  —  perfect");

        Assert.Equal("great food - perfect", result);
        Assert.DoesNotContain("  ", result!);
    }

    // ── Helper: null / empty passthrough ──────────────────────────────────────
    [Fact]
    public void StripLongDashes_Null_ReturnsNull()
    {
        Assert.Null(TypographySanitizer.StripLongDashes(null));
    }

    [Fact]
    public void StripLongDashes_Empty_ReturnsEmpty()
    {
        Assert.Equal("", TypographySanitizer.StripLongDashes(""));
    }

    // ── Helper: a string with no long dashes is unchanged ─────────────────────
    [Theory]
    [InlineData("A perfectly clean sentence.")]
    [InlineData("hyphenated-word stays intact")] // plain hyphen-minus untouched
    [InlineData("no dashes here at all")]
    public void StripLongDashes_NoLongDashes_Unchanged(string input)
    {
        Assert.Equal(input, TypographySanitizer.StripLongDashes(input));
    }

    // ── Seam GAP (a): PlanGenerationService.Sanitize now strips em-dashes ──────
    [Fact]
    public void PlanGenerationService_Sanitize_StripsEmDash()
    {
        var result = PlanGenerationService.Sanitize(
            "Romantic Miami — a weekend escape", PlanGenerationService.MaxPlanNameLength);

        Assert.Equal("Romantic Miami - a weekend escape", result);
        Assert.DoesNotContain('—', result);
    }

    // ── Seam GAP (c): PlaceTranslatorService ES drafts are stripped ───────────
    [Fact]
    public void PlaceTranslatorService_MapPlaceDraft_StripsEmDashInFreeText()
    {
        using var doc = JsonDocument.Parse("""
            {
              "name": "Café Cubano — el mejor",
              "whyThisPlace": "comida increíble — perfecto para familias",
              "bestTimes": ["mañana — temprano"],
              "neighborhood": "Little Havana",
              "bestFor": ["parejas — románticas"],
              "suitableFor": ["adultos"]
            }
            """);

        var draft = PlaceTranslatorService.MapPlaceDraft(doc.RootElement);

        Assert.Equal("Café Cubano - el mejor", draft.Name);
        Assert.Equal("comida increíble - perfecto para familias", draft.WhyThisPlace);
        Assert.Equal("mañana - temprano", draft.BestTimes![0]);
        Assert.Equal("parejas - románticas", draft.BestFor![0]);
        Assert.DoesNotContain('—', draft.Name!);
        Assert.DoesNotContain('—', draft.WhyThisPlace!);
    }

    [Fact]
    public void PlaceTranslatorService_MapPlanDraft_StripsEmDashInFreeText()
    {
        using var doc = JsonDocument.Parse("""
            {
              "name": "Fin de semana — Miami",
              "description": "un plan relajado — con mucho estilo"
            }
            """);

        var draft = PlaceTranslatorService.MapPlanDraft(doc.RootElement);

        Assert.Equal("Fin de semana - Miami", draft.Name);
        Assert.Equal("un plan relajado - con mucho estilo", draft.Description);
        Assert.DoesNotContain('—', draft.Name!);
        Assert.DoesNotContain('—', draft.Description!);
    }
}
