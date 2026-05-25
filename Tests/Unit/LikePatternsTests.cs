using LocalList.API.NET.Shared.Search;

namespace LocalList.API.Tests.Unit;

public class LikePatternsTests
{
    [Fact]
    public void Escape_EscapesPercent()
    {
        Assert.Equal(@"\%", LikePatterns.Escape("%"));
    }

    [Fact]
    public void Escape_EscapesUnderscore()
    {
        Assert.Equal(@"\_", LikePatterns.Escape("_"));
    }

    [Fact]
    public void Escape_EscapesBackslash()
    {
        Assert.Equal(@"\\", LikePatterns.Escape(@"\"));
    }

    [Fact]
    public void Escape_OrderMatters_BackslashFirst()
    {
        // "\%" should become "\\%" (backslash doubled first, then % escaped separately).
        // If % were escaped first it would become "\%" then the backslash pass would
        // double the new \ to produce "\\%" which looks the same — but the original
        // backslash would also be doubled, giving "\\\%". Verify the invariant.
        Assert.Equal(@"\\\%", LikePatterns.Escape(@"\%"));
    }

    [Fact]
    public void Escape_MixedInput()
    {
        Assert.Equal(@"100\% Pure\_Ramen", LikePatterns.Escape("100% Pure_Ramen"));
    }

    [Fact]
    public void Normalize_TrimsAndCapsAt100()
    {
        var input = "  " + new string('a', 150) + "  ";
        var result = LikePatterns.Normalize(input);
        Assert.Equal(100, result.Length);
        Assert.DoesNotContain(" ", result);
    }

    [Fact]
    public void Normalize_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LikePatterns.Normalize(null));
    }

    [Fact]
    public void Normalize_Whitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LikePatterns.Normalize("   "));
    }

    [Fact]
    public void Normalize_EscapesWildcardsAfterTrimming()
    {
        var result = LikePatterns.Normalize("  100% Ramen  ");
        Assert.Equal(@"100\% Ramen", result);
    }
}
