namespace Mersal.Migration.Core;

/// <summary>Built-in starter configs so operators have a versioned mapping to edit, not author from scratch.</summary>
public static class DefaultConfigs
{
    public static StreamConfig Providers() => new()
    {
        Stream = "providers",
        Version = "1.0.0",
        SourceSystem = "provider-contracts-xlsx",
        Mappings =
        [
            new FieldMapping("provider_id", "provider_id", Required: true),
            new FieldMapping("user_id", "user_id", Required: true),
            new FieldMapping("username", "username", Required: true),
            new FieldMapping("role", "role", Required: true),
            new FieldMapping("organization_name", "org_name", Required: false),
            new FieldMapping("location", "location", Required: false),
            new FieldMapping("contract_ref", "contract_ref", Required: false),
        ],
    };

    public static StreamConfig Beneficiaries() => new()
    {
        Stream = "beneficiaries",
        Version = "1.0.0",
        SourceSystem = "legacy-beneficiary-registry",
        Mappings =
        [
            new FieldMapping("full_name", "full_name", Required: true),
            new FieldMapping("birth_date", "birth_date", Required: false),
            new FieldMapping("national_id", "national_id", Required: false),
            new FieldMapping("unhcr_id", "unhcr_id", Required: false),
            new FieldMapping("passport", "passport", Required: false),
            new FieldMapping("policy_number", "policy_number", Required: false),
            new FieldMapping("coverage_tier", "coverage_tier", Required: false),
        ],
    };

    public static StreamConfig For(string stream) => stream switch
    {
        "providers" => Providers(),
        "beneficiaries" => Beneficiaries(),
        _ => throw new ArgumentException($"no default config for stream '{stream}'", nameof(stream)),
    };
}
