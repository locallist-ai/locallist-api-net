using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalList.API.NET.Features.Billing;

// Lenient converters for the ANALYTICS-ONLY fields of the RevenueCat webhook payload. The payload
// is UNTRUSTED, and these fields are pure reporting side-data — a type-mismatched value ("price":{},
// an array, garbage) must degrade that ONE field to null and MUST NOT abort deserialization of the
// tier-critical event (id/type/app_user_id/timestamp keep their strict binding). Dropping the event
// on a malformed analytics field would 400 → RevenueCat does NOT re-deliver (only 503 does) → a
// paying user could permanently miss their entitlement. So: never throw, always consume the value.

/// <summary>Reads a nullable decimal, returning null on any non-numeric/oversized/garbage token.</summary>
public sealed class LenientNullableDecimalConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.TryGetDecimal(out var d) ? d : null; // reader stays on the number token
            case JsonTokenType.String:
                return decimal.TryParse(reader.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s)
                    ? s : null;
            default:
                reader.Skip(); // object/array → advance to the matching end token, yield null
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

/// <summary>Reads a nullable bool, returning null on any non-boolean token.</summary>
public sealed class LenientNullableBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.String:
                return bool.TryParse(reader.GetString(), out var b) ? b : null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteBooleanValue(value.Value);
        else writer.WriteNullValue();
    }
}

/// <summary>Reads a nullable string, returning null for any non-string token (object/array/number/bool).</summary>
public sealed class LenientNullableStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Null:
                return null;
            default:
                reader.Skip(); // scalar tokens are a no-op; container tokens advance to their end
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
