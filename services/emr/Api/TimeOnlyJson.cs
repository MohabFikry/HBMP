using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mersal.Emr.Api;

/// <summary>
/// <c>TimeOnly</c> on the wire as <c>HH:mm</c> — the format this service already EMITS.
/// </summary>
/// <remarks>
/// <para><b>The defect this fixes.</b> .NET's built-in converter requires seconds: it accepts
/// <c>"09:00:00"</c> and throws on <c>"09:00"</c>. Every read endpoint here hand-formats its times as
/// <c>HH:mm</c> (<c>a.StartTime.ToString("HH\\:mm")</c>), because that is what a clinic's opening hours are.
/// So a client that read a weekly pattern and sent it back — which is exactly what an edit form does — was
/// refused by the same service that had just given it those strings, with a <c>JsonException</c> that
/// surfaces as an unhandled 500 rather than a 400 naming the field.</para>
///
/// <para>It was not only the availability editor. <c>CreateRosterException.StartTime</c> and
/// <c>EndTime</c> are the same type, so recording a PART-DAY absence — "away 11:00 to 13:00" — failed the
/// same way. That path stayed hidden because a whole-day exception sends nulls, and whole-day is the
/// default.</para>
///
/// <para><b>Why the fix is here and not in the browser.</b> Padding <c>":00"</c> onto every time at the one
/// call site that happened to break would leave the asymmetry in place for the next one. A service that
/// prints a value in one format and refuses to read it back in that format has a bug in its contract, not in
/// its callers. Seconds are still accepted, so nothing that already worked stops working.</para>
///
/// <para>Local to emr deliberately: it is the only service with <c>TimeOnly</c> in a request body. If a
/// second one appears, this belongs in <c>libs/time</c>.</para>
/// </remarks>
public sealed class HourMinuteTimeOnlyConverter : JsonConverter<TimeOnly>
{
    internal const string Wire = "HH\\:mm";

    /// <summary>Both shapes, longest first. A clinic states minutes; a machine may still send seconds.</summary>
    private static readonly string[] Accepted = ["HH:mm:ss.FFFFFFF", "HH:mm:ss", "HH:mm"];

    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (TimeOnly.TryParseExact(raw, Accepted, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        // Named, so the 400 says which value was wrong rather than "the JSON could not be converted".
        throw new JsonException($"'{raw}' is not a time of day. Expected HH:mm (seconds optional).");
    }

    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString(Wire, CultureInfo.InvariantCulture));
    }
}

/// <summary>The nullable twin. A part-day exception carries times; a whole-day one carries nulls, and null
/// has to stay null rather than becoming midnight — midnight is a real time and would read as "away from
/// 00:00", which is not what "the whole day" means to the code that checks it.</summary>
public sealed class NullableHourMinuteTimeOnlyConverter : JsonConverter<TimeOnly?>
{
    private static readonly HourMinuteTimeOnlyConverter Inner = new();

    public override TimeOnly? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeToConvert, options!);

    public override void Write(Utf8JsonWriter writer, TimeOnly? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is { } v) Inner.Write(writer, v, options!);
        else writer.WriteNullValue();
    }
}
