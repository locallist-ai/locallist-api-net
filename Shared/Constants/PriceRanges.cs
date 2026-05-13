namespace LocalList.API.NET.Shared.Constants;

public static class PriceRanges
{
    public const string Free = "FREE";
    public const string Cheap = "$";
    public const string Mid = "$$";
    public const string Expensive = "$$$";
    public const string Premium = "$$$$";

    public static readonly string[] All = [Free, Cheap, Mid, Expensive, Premium];

    public static bool IsValid(string? v) => v == null || All.Contains(v);
}
