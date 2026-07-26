using System.Text.Json;

namespace Mersal.Migration.Core;

/// <summary>One target field and where it comes from in the source, plus whether it is required.</summary>
public sealed record FieldMapping(string TargetField, string SourceField, bool Required);

/// <summary>
/// Versioned, per-stream migration config (phase 12.1). The <see cref="Version"/> is recorded on
/// every batch so a run is reproducible and reconciliation ties back to the exact mapping used.
/// Loadable from JSON so operators edit config, not code.
/// </summary>
public sealed record StreamConfig
{
    public required string Stream { get; init; }
    public required string Version { get; init; }
    public required string SourceSystem { get; init; }
    public required IReadOnlyList<FieldMapping> Mappings { get; init; }

    public IEnumerable<FieldMapping> Required => Mappings.Where(m => m.Required);

    public static StreamConfig FromJson(string json)
    {
        var config = JsonSerializer.Deserialize<StreamConfig>(json, Options)
            ?? throw new FormatException("stream config JSON did not deserialize");
        if (string.IsNullOrWhiteSpace(config.Stream) || string.IsNullOrWhiteSpace(config.Version))
            throw new FormatException("stream config requires 'stream' and 'version'");
        return config;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
