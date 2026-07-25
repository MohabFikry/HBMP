using System.Globalization;

namespace Mersal.Admin.Domain;

/// <summary>Validates + canonicalizes a system-config value against its declared type (typed, validated settings).
/// A value that doesn't parse as its type is rejected before it can be stored (so a downstream reader never sees a
/// malformed threshold/flag).</summary>
public static class ConfigValidation
{
    public sealed record Result(bool Ok, string? Canonical, string? Error);

    public static Result Validate(ConfigValueType type, string? raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0) return new(false, null, "value-required");

        switch (type)
        {
            case ConfigValueType.Text:
                return new(true, value, null);
            case ConfigValueType.Whole:
                return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    ? new(true, i.ToString(CultureInfo.InvariantCulture), null) : new(false, null, "not-an-integer");
            case ConfigValueType.Number:
                return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
                    ? new(true, d.ToString(CultureInfo.InvariantCulture), null) : new(false, null, "not-a-decimal");
            case ConfigValueType.Boolean:
                return bool.TryParse(value, out var b)
                    ? new(true, b ? "true" : "false", null) : new(false, null, "not-a-boolean");
            case ConfigValueType.Duration:
                return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts)
                    ? new(true, ts.ToString(), null) : new(false, null, "not-a-duration");
            default:
                return new(false, null, "unknown-type");
        }
    }
}
