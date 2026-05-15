using System.Text.RegularExpressions;

namespace LocalList.API.NET.Shared.Observability;

public static partial class PiiRedactor
{
    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\+?[\d\s\-().]{7,15}\d", RegexOptions.Compiled)]
    private static partial Regex PhonePattern();

    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var result = EmailPattern().Replace(input, "[REDACTED:email]");
        result = PhonePattern().Replace(result, "[REDACTED:phone]");
        return result;
    }
}
