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
}
