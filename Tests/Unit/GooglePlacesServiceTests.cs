using LocalList.API.NET.Features.Admin.Places;

namespace LocalList.API.Tests.Unit;

public class GooglePlacesServiceTests
{
    [Theory]
    [InlineData("PRICE_LEVEL_FREE", "FREE")]
    [InlineData("PRICE_LEVEL_INEXPENSIVE", "$")]
    [InlineData("PRICE_LEVEL_MODERATE", "$$")]
    [InlineData("PRICE_LEVEL_EXPENSIVE", "$$$")]
    [InlineData("PRICE_LEVEL_VERY_EXPENSIVE", "$$$$")]
    [InlineData(null, null)]
    [InlineData("PRICE_LEVEL_UNSPECIFIED", null)]
    [InlineData("UNKNOWN", null)]
    public void MapPriceLevel_MapsCorrectly(string? input, string? expected) =>
        Assert.Equal(expected, GooglePlacesService.MapPriceLevel(input));
}
