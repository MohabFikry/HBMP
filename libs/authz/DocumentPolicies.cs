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

    /// <summary>Download the BYTES of an operational document — a bulk upload, its error report, an extract.</summary>
    /// <remarks>
    /// The upload required <c>document:write</c>; the download required only that you were signed in. Every
    /// operational kind is PHI-bearing — a bulk error report quotes hundreds of member numbers, which is why
    /// the endpoint audits each download as an Export — so any authenticated token in the tenant, of any
    /// role, could enumerate ids and stream lists of identified people. RLS scoped that to the tenant and
    /// nothing scoped it further.
    ///
    /// Gated on <c>document:write</c> deliberately rather than on a new scope: the grant belongs to
    /// <c>beneficiary_mgmt</c> and <c>beneficiary_mgmt_supervisor</c> alone (migration 0016), which is
    /// exactly the set that runs bulk membership operations and therefore produces these files. The
    /// authority to create the artifact is the authority to read it back, and inventing a second scope
    /// would mean seeding a grant with no distinct holder.
    /// </remarks>
    public const string OperationalRead = "document:operational-read";

    /// <summary>Download the BYTES of a beneficiary's CLINICAL document — a signed report, a study, a scan.</summary>
    /// <remarks>
    /// <para>Separate from <see cref="Read"/> because that action is metadata and says so: "min-necessary —
    /// never blob bytes". Knowing a result report exists and being able to read it are different
    /// disclosures, and the role lists differ — reception and beneficiary management legitimately see that a
    /// beneficiary has documents on file without being people who read radiology reports.</para>
    ///
    /// <para><b>This is not the whole gate for a result report.</b> A report attached to an investigation
    /// line is additionally subject to the 14.7 sensitivity gate, which lives in orders-service because it
    /// turns on the LINE's sensitivity and the caller's time-boxed grants — facts this service does not have.
    /// The clinician path is <c>GET /investigation-orders/{orderId}/lines/{lineId}/result/report</c>, which
    /// applies that gate and then calls here with the caller's own bearer, so both checks run. This rule is
    /// the second layer, not the first, and the document id needed to reach it is only ever handed out by the
    /// first.</para>
    /// </remarks>
    public const string ContentRead = "document:content-read";

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
        new PolicyRule
        {
            Action = OperationalRead, ResourceType = Resource,
            Scopes = Set("document:write"),
            RequiredConditions = [AbacConditions.TenantMatch], Sensitive = true,
        },
        // Narrower than Read on purpose — see ContentRead. These are the roles that may read a clinical
        // result at all (11-permission-matrix §3.2): the treating clinician and the oversight tiers. Nurses
        // are absent because they read results through the profile projection, which is field-scoped, rather
        // than as raw files; reception, beneficiary management and org_admin are absent because seeing that a
        // document is on file is the whole of what their row grants.
        new PolicyRule
        {
            Action = ContentRead, ResourceType = Resource,
            Roles = Set("doctor", "medical_approval", "medical_director"),
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
