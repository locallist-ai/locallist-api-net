namespace LocalList.API.NET.Shared.Taxonomy;

public static class PlaceTaxonomy
{
    public static readonly IReadOnlyList<string> Categories = new[]
    {
        "Food", "Nightlife", "Coffee", "Outdoors", "Wellness", "Culture", "Shopping"
    };

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> SubcategoriesByCategory =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Food"]     = new[] { "Ramen", "Sushi", "Italian", "Pizza", "Mexican", "Tacos", "Cuban", "Latin American", "American", "Steakhouse", "Seafood", "Mediterranean", "Asian Fusion", "Brunch", "Bakery", "Vegan" },
            ["Nightlife"] = new[] { "Cocktail Bar", "Speakeasy", "Rooftop Bar", "Wine Bar", "Sports Bar", "Beer Bar", "Nightclub", "Live Music" },
            ["Coffee"]   = new[] { "Specialty Coffee", "Espresso Bar", "Bakery Café", "Tea House", "Juice Bar", "Dessert" },
            ["Outdoors"] = new[] { "Beach", "Park", "Garden", "Trail", "Marina", "Pier", "Waterfront", "Dog Park" },
            ["Wellness"] = new[] { "Spa", "Pilates", "Yoga", "Gym", "Sauna", "IV Therapy", "Massage", "Salt Cave" },
            ["Culture"]  = new[] { "Museum", "Gallery", "Theater", "Music Venue", "Festival Site", "Historic Site", "Public Art", "Cultural Center" },
            ["Shopping"] = new[] { "Boutique", "Vintage", "Bookstore", "Record Store", "Concept Store", "Market", "Florist", "Designer" }
        };

    public static bool IsValidCategory(string category) =>
        Categories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Null/empty subcategory is always valid (optional field).
    /// Non-empty subcategory must belong to the allowed list for the given category.
    /// </summary>
    public static bool IsValidSubcategory(string category, string? subcategory)
    {
        if (string.IsNullOrWhiteSpace(subcategory)) return true;
        if (!SubcategoriesByCategory.TryGetValue(category, out var allowed)) return false;
        return allowed.Any(s => string.Equals(s, subcategory, StringComparison.OrdinalIgnoreCase));
    }

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

    /// <summary>
    /// Infers a canonical subcategory from Google Places types[].
    /// A "taco" keyword in the place name overrides the type-based mapping for Food.
    /// Returns null when no mapping is found.
    /// </summary>
    public static string? CanonicalSubcategoryFromGoogleTypes(
        string category, IEnumerable<string> googleTypes, string? placeName = null)
    {
        if (!string.IsNullOrWhiteSpace(placeName) &&
            string.Equals(category, "Food", StringComparison.OrdinalIgnoreCase) &&
            placeName.Contains("taco", StringComparison.OrdinalIgnoreCase))
            return "Tacos";

        foreach (var type in googleTypes)
        {
            if (_googleTypeToSubcategory.TryGetValue(type, out var candidate) &&
                IsValidSubcategory(category, candidate))
                return candidate;
        }
        return null;
    }
}
