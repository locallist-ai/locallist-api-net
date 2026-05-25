namespace LocalList.API.NET.Shared.Taxonomy;

public static class PlaceTaxonomy
{
    public static readonly IReadOnlyList<string> Categories = new[]
    {
        "Food", "Nightlife", "Coffee", "Outdoors", "Wellness", "Culture", "Shopping"
    };

    public const string GooglePlaceholderWhyThisPlace = "Imported from Google Places. Pending curatorial copy.";

    public static bool IsPlaceholderOrEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) || s.Trim() == GooglePlaceholderWhyThisPlace;

    public static bool IsValidCategory(string category) =>
        Categories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));

    // Google Places types[] → canonical subcategory
    private static readonly Dictionary<string, string> _googleTypeToSubcategory =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Food
            ["ramen_restaurant"]         = "Ramen",
            ["sushi_restaurant"]         = "Sushi",
            ["japanese_restaurant"]      = "Sushi",
            ["italian_restaurant"]       = "Italian",
            ["pizza_restaurant"]         = "Pizza",
            ["mexican_restaurant"]       = "Mexican",
            ["american_restaurant"]      = "American",
            ["hamburger_restaurant"]     = "American",
            ["steak_house"]              = "Steakhouse",
            ["seafood_restaurant"]       = "Seafood",
            ["mediterranean_restaurant"] = "Mediterranean",
            ["asian_restaurant"]         = "Asian Fusion",
            ["korean_restaurant"]        = "Asian Fusion",
            ["chinese_restaurant"]       = "Asian Fusion",
            ["thai_restaurant"]          = "Asian Fusion",
            ["vietnamese_restaurant"]    = "Asian Fusion",
            ["brunch_restaurant"]        = "Brunch",
            ["breakfast_restaurant"]     = "Brunch",
            ["bakery"]                   = "Bakery",
            ["vegan_restaurant"]         = "Vegan",
            ["cuban_restaurant"]         = "Cuban",
            ["latin_american_restaurant"] = "Latin American",
            // Nightlife
            ["pub"]                      = "Pub",
            ["bar"]                      = "Cocktail Bar",
            ["cocktail_bar"]             = "Cocktail Bar",
            ["wine_bar"]                 = "Wine Bar",
            ["sports_bar"]               = "Sports Bar",
            ["night_club"]               = "Nightclub",
            // Coffee
            ["coffee_shop"]              = "Specialty Coffee",
            ["cafe"]                     = "Specialty Coffee",
            ["tea_house"]                = "Tea House",
            ["dessert_shop"]             = "Dessert",
            ["ice_cream_shop"]           = "Dessert",
            ["juice_shop"]               = "Juice Bar",
            // Outdoors
            ["beach"]                    = "Beach",
            ["park"]                     = "Park",
            ["national_park"]            = "Park",
            ["botanical_garden"]         = "Garden",
            ["marina"]                   = "Marina",
            ["pier"]                     = "Pier",
            ["dog_park"]                 = "Dog Park",
            ["hiking_area"]              = "Trail",
            // Wellness
            ["spa"]                      = "Spa",
            ["yoga_studio"]              = "Yoga",
            ["gym"]                      = "Gym",
            ["fitness_center"]           = "Gym",
            ["massage"]                  = "Massage",
            ["pilates_studio"]           = "Pilates",
            // Culture
            ["museum"]                   = "Museum",
            ["art_gallery"]              = "Gallery",
            ["theater"]                  = "Theater",
            ["performing_arts_theater"]  = "Theater",
            ["event_venue"]              = "Music Venue",
            ["concert_hall"]             = "Music Venue",
            ["cultural_center"]          = "Cultural Center",
            ["historical_landmark"]      = "Historic Site",
            ["monument"]                 = "Historic Site",
            // Shopping
            ["clothing_store"]           = "Boutique",
            ["book_store"]               = "Bookstore",
            ["market"]                   = "Market",
            ["florist"]                  = "Florist",
            ["department_store"]         = "Concept Store",
            ["shopping_mall"]            = "Concept Store",
            ["record_store"]             = "Record Store",
        };

    public static IEnumerable<string> SubcategoryMappingKeys => _googleTypeToSubcategory.Keys;

    // Maps Google primaryType / types[] to a LocalList canonical category.
    private static readonly Dictionary<string, string> _googleTypeToCategory =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["restaurant"] = "Food", ["meal_takeaway"] = "Food", ["meal_delivery"] = "Food",
            ["ramen_restaurant"] = "Food", ["sushi_restaurant"] = "Food",
            ["japanese_restaurant"] = "Food", ["italian_restaurant"] = "Food",
            ["pizza_restaurant"] = "Food", ["mexican_restaurant"] = "Food",
            ["american_restaurant"] = "Food", ["hamburger_restaurant"] = "Food",
            ["steak_house"] = "Food", ["seafood_restaurant"] = "Food",
            ["mediterranean_restaurant"] = "Food", ["asian_restaurant"] = "Food",
            ["korean_restaurant"] = "Food", ["chinese_restaurant"] = "Food",
            ["thai_restaurant"] = "Food", ["vietnamese_restaurant"] = "Food",
            ["brunch_restaurant"] = "Food", ["breakfast_restaurant"] = "Food",
            ["bakery"] = "Food", ["vegan_restaurant"] = "Food",
            ["cuban_restaurant"] = "Food", ["latin_american_restaurant"] = "Food",
            ["pub"] = "Nightlife", ["bar"] = "Nightlife", ["cocktail_bar"] = "Nightlife",
            ["wine_bar"] = "Nightlife", ["sports_bar"] = "Nightlife", ["night_club"] = "Nightlife",
            ["coffee_shop"] = "Coffee", ["cafe"] = "Coffee", ["tea_house"] = "Coffee",
            ["dessert_shop"] = "Coffee", ["ice_cream_shop"] = "Coffee", ["juice_shop"] = "Coffee",
            ["beach"] = "Outdoors", ["park"] = "Outdoors", ["national_park"] = "Outdoors",
            ["botanical_garden"] = "Outdoors", ["marina"] = "Outdoors", ["pier"] = "Outdoors",
            ["dog_park"] = "Outdoors", ["hiking_area"] = "Outdoors",
            ["spa"] = "Wellness", ["yoga_studio"] = "Wellness", ["gym"] = "Wellness",
            ["fitness_center"] = "Wellness", ["massage"] = "Wellness", ["pilates_studio"] = "Wellness",
            ["museum"] = "Culture", ["art_gallery"] = "Culture", ["theater"] = "Culture",
            ["performing_arts_theater"] = "Culture", ["event_venue"] = "Culture",
            ["concert_hall"] = "Culture", ["cultural_center"] = "Culture",
            ["historical_landmark"] = "Culture", ["monument"] = "Culture",
            ["clothing_store"] = "Shopping", ["book_store"] = "Shopping", ["market"] = "Shopping",
            ["florist"] = "Shopping", ["department_store"] = "Shopping",
            ["shopping_mall"] = "Shopping", ["record_store"] = "Shopping",
        };

    /// <summary>
    /// Infers a canonical LocalList category from Google Places primaryType + types[].
    /// Returns null when no mapping is found.
    /// </summary>
    public static string? CategoryFromGoogleTypes(string? primaryType, IEnumerable<string>? googleTypes)
    {
        var types = new List<string>();
        if (!string.IsNullOrEmpty(primaryType)) types.Add(primaryType);
        if (googleTypes != null) types.AddRange(googleTypes);

        foreach (var type in types)
        {
            if (_googleTypeToCategory.TryGetValue(type, out var cat))
                return cat;
        }
        return null;
    }

    /// <summary>
    /// Infers a canonical subcategory from Google Places types[].
    /// A "taco" keyword in the place name overrides the type-based mapping for Food.
    /// Pass the list of allowed subcategory keys/labels from ITaxonomyService.
    /// Returns null when no mapping is found.
    /// </summary>
    public static string? CanonicalSubcategoryFromGoogleTypes(
        string category,
        IEnumerable<string> googleTypes,
        IReadOnlyList<string> allowedSubs,
        string? placeName = null)
    {
        if (!string.IsNullOrWhiteSpace(placeName) &&
            string.Equals(category, "Food", StringComparison.OrdinalIgnoreCase) &&
            placeName.Contains("taco", StringComparison.OrdinalIgnoreCase))
            return "Tacos";

        foreach (var type in googleTypes)
        {
            if (_googleTypeToSubcategory.TryGetValue(type, out var candidate) &&
                allowedSubs.Any(s => string.Equals(s, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return null;
    }
}
