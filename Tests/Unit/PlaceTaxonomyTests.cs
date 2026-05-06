using LocalList.API.NET.Shared.Taxonomy;

namespace LocalList.API.Tests.Unit;

public class PlaceTaxonomyTests
{
    [Fact]
    public void Categories_HasSevenEntries()
    {
        Assert.Equal(7, PlaceTaxonomy.Categories.Count);
        Assert.Contains("Shopping", PlaceTaxonomy.Categories);
    }

    [Fact]
    public void SubcategoriesByCategory_HasEntriesForAllCategories()
    {
        foreach (var cat in PlaceTaxonomy.Categories)
            Assert.True(PlaceTaxonomy.SubcategoriesByCategory.ContainsKey(cat),
                $"Missing subcategory bucket for '{cat}'");
    }

    [Fact]
    public void TotalSubcategoryCount_IsAtLeast62()
    {
        var total = PlaceTaxonomy.SubcategoriesByCategory.Values.Sum(v => v.Count);
        Assert.True(total >= 62, $"Expected ≥62 subcategories, got {total}");
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
    [InlineData("Food", null, true)]         // null always valid
    [InlineData("Food", "", true)]            // empty always valid
    [InlineData("Food", "Ramen", true)]
    [InlineData("Food", "ramen", true)]       // case-insensitive
    [InlineData("Food", "InvalidThing", false)]
    [InlineData("Coffee", "Specialty Coffee", true)]
    [InlineData("Coffee", "Ramen", false)]    // wrong category
    [InlineData("Shopping", "Boutique", true)]
    [InlineData("Shopping", "Pizza", false)]
    public void IsValidSubcategory_VariousCases(string category, string? sub, bool expected) =>
        Assert.Equal(expected, PlaceTaxonomy.IsValidSubcategory(category, sub));

    [Theory]
    [InlineData("Food", new[] { "ramen_restaurant" }, null, "Ramen")]
    [InlineData("Food", new[] { "sushi_restaurant" }, null, "Sushi")]
    [InlineData("Food", new[] { "italian_restaurant" }, null, "Italian")]
    [InlineData("Food", new[] { "pizza_restaurant" }, null, "Pizza")]
    [InlineData("Food", new[] { "mexican_restaurant" }, null, "Mexican")]
    [InlineData("Food", new[] { "mexican_restaurant" }, "Taco Bros", "Tacos")] // name override
    [InlineData("Nightlife", new[] { "cocktail_bar" }, null, "Cocktail Bar")]
    [InlineData("Nightlife", new[] { "night_club" }, null, "Nightclub")]
    [InlineData("Coffee", new[] { "coffee_shop" }, null, "Specialty Coffee")]
    [InlineData("Outdoors", new[] { "beach" }, null, "Beach")]
    [InlineData("Wellness", new[] { "spa" }, null, "Spa")]
    [InlineData("Culture", new[] { "museum" }, null, "Museum")]
    [InlineData("Shopping", new[] { "clothing_store" }, null, "Boutique")]
    [InlineData("Food", new[] { "unknown_type" }, null, null)] // no mapping
    [InlineData("Food", new[] { "coffee_shop" }, null, null)]  // type doesn't belong to Food
    public void CanonicalSubcategoryFromGoogleTypes_MapsCorrectly(
        string category, string[] types, string? name, string? expected) =>
        Assert.Equal(expected, PlaceTaxonomy.CanonicalSubcategoryFromGoogleTypes(category, types, name));
}
