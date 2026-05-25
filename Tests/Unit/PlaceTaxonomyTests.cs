using LocalList.API.NET.Shared.Taxonomy;

namespace LocalList.API.Tests.Unit;

public class PlaceTaxonomyTests
{
    // All canonical subcategory labels known to PlaceTaxonomy (used as allowedSubs in tests)
    private static readonly IReadOnlyList<string> AllKnownSubs = new[]
    {
        "Ramen", "Sushi", "Italian", "Pizza", "Mexican", "American", "Steakhouse", "Seafood",
        "Mediterranean", "Asian Fusion", "Brunch", "Bakery", "Vegan", "Cuban", "Latin American",
        "Tacos", "Pub", "Cocktail Bar", "Wine Bar", "Sports Bar", "Nightclub",
        "Specialty Coffee", "Tea House", "Dessert", "Juice Bar",
        "Beach", "Park", "Garden", "Marina", "Pier", "Dog Park", "Trail",
        "Spa", "Yoga", "Gym", "Massage", "Pilates",
        "Museum", "Gallery", "Theater", "Music Venue", "Cultural Center", "Historic Site",
        "Boutique", "Bookstore", "Market", "Florist", "Concept Store", "Record Store",
    };

    [Fact]
    public void Categories_HasSevenEntries()
    {
        Assert.Equal(7, PlaceTaxonomy.Categories.Count);
        Assert.Contains("Shopping", PlaceTaxonomy.Categories);
    }

    [Fact]
    public void IsPlaceholderOrEmpty_DetectsPlaceholderAndEmpty()
    {
        Assert.True(PlaceTaxonomy.IsPlaceholderOrEmpty(null));
        Assert.True(PlaceTaxonomy.IsPlaceholderOrEmpty(""));
        Assert.True(PlaceTaxonomy.IsPlaceholderOrEmpty("   "));
        Assert.True(PlaceTaxonomy.IsPlaceholderOrEmpty(PlaceTaxonomy.GooglePlaceholderWhyThisPlace));
        Assert.False(PlaceTaxonomy.IsPlaceholderOrEmpty("A real description."));
    }

    [Theory]
    [InlineData("Food", true)]
    [InlineData("food", true)]
    [InlineData("NIGHTLIFE", true)]
    [InlineData("Shopping", true)]
    [InlineData("Drinks", false)]
    [InlineData("", false)]
    public void IsValidCategory_VariousCases(string category, bool expected) =>
        Assert.Equal(expected, PlaceTaxonomy.IsValidCategory(category));

    [Theory]
    [InlineData("Food", new[] { "ramen_restaurant" }, null, "Ramen")]
    [InlineData("Food", new[] { "sushi_restaurant" }, null, "Sushi")]
    [InlineData("Food", new[] { "italian_restaurant" }, null, "Italian")]
    [InlineData("Food", new[] { "pizza_restaurant" }, null, "Pizza")]
    [InlineData("Food", new[] { "mexican_restaurant" }, null, "Mexican")]
    [InlineData("Food", new[] { "mexican_restaurant" }, "Taco Bros", "Tacos")] // name override
    [InlineData("Nightlife", new[] { "cocktail_bar" }, null, "Cocktail Bar")]
    [InlineData("Nightlife", new[] { "night_club" }, null, "Nightclub")]
    [InlineData("Nightlife", new[] { "pub" }, null, "Pub")]
    [InlineData("Coffee", new[] { "coffee_shop" }, null, "Specialty Coffee")]
    [InlineData("Outdoors", new[] { "beach" }, null, "Beach")]
    [InlineData("Wellness", new[] { "spa" }, null, "Spa")]
    [InlineData("Culture", new[] { "museum" }, null, "Museum")]
    [InlineData("Shopping", new[] { "clothing_store" }, null, "Boutique")]
    [InlineData("Food", new[] { "unknown_type" }, null, null)] // no mapping
    public void CanonicalSubcategoryFromGoogleTypes_MapsCorrectly(
        string category, string[] types, string? name, string? expected) =>
        Assert.Equal(expected, PlaceTaxonomy.CanonicalSubcategoryFromGoogleTypes(category, types, AllKnownSubs, name));

    [Fact]
    public void CanonicalSubcategoryFromGoogleTypes_CrossCategory_ReturnsNullWhenSubNotAllowed()
    {
        // "coffee_shop" maps to "Specialty Coffee", but that sub is not in Food's allowed list.
        // In production allowedSubs is filtered per-category by TaxonomyService.GetByCategoryAsync.
        var foodOnlySubs = new[] { "Ramen", "Sushi", "Italian", "Pizza", "Mexican", "Tacos", "Cuban", "Latin American", "American", "Steakhouse", "Seafood", "Mediterranean", "Asian Fusion", "Brunch", "Bakery", "Vegan" };
        Assert.Null(PlaceTaxonomy.CanonicalSubcategoryFromGoogleTypes("Food", ["coffee_shop"], foodOnlySubs, null));
    }

    [Theory]
    [InlineData("restaurant", null, "Food")]
    [InlineData("bar", null, "Nightlife")]
    [InlineData("pub", null, "Nightlife")]
    [InlineData("cafe", null, "Coffee")]
    [InlineData("museum", null, "Culture")]
    [InlineData("park", null, "Outdoors")]
    [InlineData("spa", null, "Wellness")]
    [InlineData("clothing_store", null, "Shopping")]
    [InlineData("unknown_type", null, null)]
    public void CategoryFromGoogleTypes_MapsCorrectly(string primaryType, string[]? extraTypes, string? expected) =>
        Assert.Equal(expected, PlaceTaxonomy.CategoryFromGoogleTypes(primaryType, extraTypes));

    [Fact]
    public void TaxonomyConsistency_GoogleTypeSubcategoriesMustBePresentInAllKnownSubs()
    {
        // Every subcategory label returned by CanonicalSubcategoryFromGoogleTypes must be
        // in AllKnownSubs (which mirrors the DB seed). This prevents silent drift between
        // _googleTypeToSubcategory and the seeded subcategory list.
        foreach (var googleType in PlaceTaxonomy.SubcategoryMappingKeys)
        {
            var category = PlaceTaxonomy.CategoryFromGoogleTypes(googleType, null);
            Assert.True(category != null,
                $"Google type '{googleType}' has a subcategory mapping but no category mapping.");

            var subcategory = PlaceTaxonomy.CanonicalSubcategoryFromGoogleTypes(
                category!, new[] { googleType }, AllKnownSubs);
            Assert.True(subcategory != null,
                $"Google type '{googleType}' maps to a subcategory that is not in AllKnownSubs.");
        }
    }
}
