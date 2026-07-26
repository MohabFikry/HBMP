namespace Mersal.Authz;

/// <summary>Document-service policy overlay (16.6, H9). Reads of a beneficiary's document metadata are PHI and
/// were previously ungated (any <c>document:write</c> holder could list ANY beneficiary's documents, unaudited).
/// The read rule is role-scoped + tenant-scoped and marked <see cref="PolicyRule.Sensitive"/>, so the engine
/// records an audited (attempted-)PHI-access event on both deny and allow. The coarse OAuth <c>document:read</c>
/// scope split rides on the 17.5 scope reconciliation (the SPA must request it); until then the read is gated by
/// ROLE (already in the fail-closed token) + tenant, which is the substantive min-necessary control.</summary>
public static class DocumentPolicies
{
    public const string Version = "16.6";
    public const string Resource = "document";

    /// <summary>List/read a beneficiary's document metadata (min-necessary — never blob bytes).</summary>
    public const string Read = "document:read";
    /// <summary>Upload / version a beneficiary's document.</summary>
    public const string Write = "document:write";

    // Roles that legitimately view a beneficiary's registration/clinical documents. super_admin is intentionally
    // absent — global PHI reach is only via break-glass (the break-glass ABAC condition elevates when active).
    private static readonly string[] Readers =
        ["reception", "beneficiary_mgmt", "doctor", "nurse", "medical_approval", "medical_director", "case_manager", "org_admin"];

    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        new PolicyRule
        {
            Action = Read, ResourceType = Resource, Roles = Set(Readers),
            RequiredConditions = [AbacConditions.TenantMatch], Sensitive = true,
        },
        new PolicyRule
        {
            Action = Write, ResourceType = Resource,
            Scopes = Set("document:write"),
            RequiredConditions = [AbacConditions.TenantMatch], Sensitive = true,
        },
    ];

    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
