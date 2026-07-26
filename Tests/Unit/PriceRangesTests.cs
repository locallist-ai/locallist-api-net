using LocalList.API.NET.Shared.Constants;

namespace LocalList.API.Tests.Unit;

public class PriceRangesTests
{
    [Theory]
    [InlineData("FREE", true)]
    [InlineData("$", true)]
    [InlineData("$$", true)]
    [InlineData("$$$", true)]
    [InlineData("$$$$", true)]
    [InlineData(null, true)]
    [InlineData("PWYC", false)]
    [InlineData("€", false)]
    [InlineData("free", false)]
    [InlineData("", false)]
    public void IsValid_VariousCases(string? value, bool expected) =>
        Assert.Equal(expected, PriceRanges.IsValid(value));

    // ── TryNormalize (fix/price-range-validation) ────────────────────────────

    [Theory]
    [InlineData("FREE", "FREE")]
    [InlineData("free", "FREE")]
    [InlineData("Free", "FREE")]
    [InlineData(" FREE ", "FREE")]
    [InlineData("$", "$")]
    [InlineData(" $ ", "$")]
    [InlineData("$$", "$$")]
    [InlineData("$$$", "$$$")]
    [InlineData("$$$$", "$$$$")]
    public void TryNormalize_CanonicalizesValidValues(string raw, string expected)
    {
        Assert.True(PriceRanges.TryNormalize(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_EmptyOrWhitespace_IsValidNull(string? raw)
    {
        Assert.True(PriceRanges.TryNormalize(raw, out var normalized));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("€€")]
    [InlineData("cheap")]
    [InlineData("PWYC")]
    [InlineData("$$$$$")]
    [InlineData("gratis")]
    public void TryNormalize_NonCanonical_ReturnsFalseAndNull(string raw)
    {
        Assert.False(PriceRanges.TryNormalize(raw, out var normalized));
        Assert.Null(normalized);
    }
}
