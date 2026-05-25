namespace LocalList.API.NET.Shared.Search;

public static class LikePatterns
{
    public const int MaxSearchLength = 100;

    /// <summary>
    /// Escapes LIKE/ILIKE wildcards (%, _) and the default Postgres escape char (\)
    /// so the user-supplied fragment is treated as a literal substring.
    /// Pair with <c>EF.Functions.ILike(col, $"%{escaped}%", @"\")</c> to
    /// bind the escape character explicitly — never rely on provider defaults.
    /// </summary>
    public static string Escape(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        // Order matters: replace backslash first, then % and _, otherwise the newly
        // added backslashes would themselves be doubled in a second pass.
        return input.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
    }

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var trimmed = input.Trim();
        if (trimmed.Length > MaxSearchLength) trimmed = trimmed[..MaxSearchLength];
        return Escape(trimmed);
    }
}
