using LocalList.API.NET.Features.Builder.Services;

namespace LocalList.API.Tests.Unit;

public class EmbeddingServiceTests
{
    [Fact]
    public void BuildPlaceIndexText_ComposesAllFields()
    {
        var text = EmbeddingService.BuildPlaceIndexText(
            name: "Enriqueta's Sandwich Shop",
            category: "Food",
            subcategory: "Cuban",
            neighborhood: "Wynwood",
            city: "Miami",
            whyThisPlace: "Cuban breakfast sandwich",
            bestFor: new[] { "solo", "quick" },
            suitableFor: new[] { "couple" });

        Assert.Contains("Enriqueta's Sandwich Shop", text);
        Assert.Contains("Miami", text);
        Assert.Contains("Wynwood", text);
        Assert.Contains("Food", text);
        Assert.Contains("Cuban", text);
        Assert.Contains("Cuban breakfast sandwich", text);
        Assert.Contains("solo quick", text);
        Assert.Contains("couple", text);
    }

    [Fact]
    public void BuildPlaceIndexText_SkipsEmptyFields()
    {
        var text = EmbeddingService.BuildPlaceIndexText(
            name: "Solo Name",
            category: null,
            subcategory: null,
            neighborhood: "",
            city: null,
            whyThisPlace: null,
            bestFor: null,
            suitableFor: null);

        Assert.Equal("Solo Name", text);
    }

    [Fact]
    public void BuildPlaceIndexText_DifferentCitiesYieldDifferentText()
    {
        var miami = EmbeddingService.BuildPlaceIndexText(
            name: "Joe's Pizza", category: "Food", subcategory: "Pizza", neighborhood: "Downtown",
            city: "Miami", whyThisPlace: "slice", bestFor: null, suitableFor: null);
        var nyc = EmbeddingService.BuildPlaceIndexText(
            name: "Joe's Pizza", category: "Food", subcategory: "Pizza", neighborhood: "Downtown",
            city: "NYC", whyThisPlace: "slice", bestFor: null, suitableFor: null);

        Assert.NotEqual(miami, nyc);
        Assert.Contains("Miami", miami);
        Assert.Contains("NYC", nyc);
    }

    [Fact]
    public void BuildPlaceIndexText_JoinsWithPeriodSeparator()
    {
        var text = EmbeddingService.BuildPlaceIndexText(
            name: "A",
            category: "B",
            subcategory: null,
            neighborhood: null,
            city: null,
            whyThisPlace: "C",
            bestFor: null,
            suitableFor: null);

        Assert.Equal("A. B. C", text);
    }
}
