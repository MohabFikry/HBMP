namespace Mersal.Authz;

using Classes = DefaultPolicies.Classes;

/// <summary>
/// Phase 18.B3 (audit R2 S6) — the beneficiary-directory policy overlay.
///
/// patient-service serves the master beneficiary record: legal name, member number, and the identifier set —
/// national ID, UNHCR registration number, passport. For a refugee population that identifier set is the most
/// dangerous data the platform holds. It is not clinical, so it attracts less caution than a diagnosis, but a
/// leaked UNHCR number is a person locatable by an authority they fled; a diagnosis is not.
///
/// Before this, reading it required <c>patient:write</c> and nothing else: no engine call, no row scope, no
/// audit, no field projection. Two consequences, in opposite directions.
///   • Nobody could do their job. Only <c>beneficiary_mgmt</c> holds <c>patient:write</c> in the seed, so
///     reception — whose entire task is finding the member at the desk — could not read a beneficiary at all,
///     and every other role fell back to the call-centre or EMR composites.
///   • Whoever COULD read, read everything. One scope covered create, amend, search and disclose alike, with
///     no record that a PHI read had happened. §19 requires a PHI read to be audited; this one was not.
///
/// So the split is not bureaucratic tidying: <c>patient:read</c> is what reception should have had, and
/// <c>patient:write</c> is what should never have been the price of looking someone up.
/// </summary>
public static class PatientPolicies
{
    public const string Version = "18.B3";

    /// <summary>Read a beneficiary by id or through search. Sensitive → the engine audits the allow, which is
    /// the PHI-read record §19 asks for.</summary>
    public const string ReadBeneficiary = "patient:read-beneficiary";

    public const string Resource = "beneficiary";

    /// <summary>Roles with a legitimate directory read. Reception and the call centre identify the caller at
    /// the door; clinicians confirm they have the right person; case managers coordinate; claims and finance
    /// are ABSENT — they work from a claim's beneficiary reference, never the directory (10-role-matrix §3.17:
    /// the claims side never sees clinical context, and it does not need the identifier set either).</summary>
    private static readonly string[] Readers =
    [
        "reception", "call_center", "beneficiary_mgmt", "beneficiary_mgmt_supervisor", "case_manager",
        "doctor", "nurse", "medical_approval", "medical_director",
        // policy_admin enrols people into policies, and policy-service refuses to enrol without first reading
        // the beneficiary's status — deliberately not fail-soft, because it cannot otherwise tell an Active
        // member from a Blocked one. But no enrolment-capable role was a beneficiary reader, so the status probe
        // 403'd and EVERY enrolment through the API failed with an unhandled 500. That is why the only
        // enrollments in the dev database belong to beneficiaries patient-service has never heard of: they were
        // written straight to the table because the API could not do it. Knowing who you are enrolling, and
        // whether they are active, is squarely this role's job; the read stays tenant-gated, field-projected
        // and audited as Sensitive like every other.
        "policy_admin",
    ];

    public static IReadOnlyList<PolicyRule> Rules() =>
    [
        new PolicyRule
        {
            Action = ReadBeneficiary, ResourceType = Resource,
            Roles = Set(Readers), Scopes = Set("patient:read"),
            RequiredConditions = [AbacConditions.TenantMatch],
            Sensitive = true,
        },
    ];

    /// <summary>Full bundle = platform defaults + the beneficiary-directory rules.</summary>
    public static PolicyBundle Bundle()
    {
        var baseBundle = DefaultPolicies.Bundle();
        return new PolicyBundle(Version, [.. baseBundle.Rules, .. Rules()]);
    }

    /// <summary>
    /// Role → readable field-classes for the beneficiary record, layered over the platform default matrix.
    ///
    /// Two classes the R1 audit deferred are modelled here, because "identity" as one bucket cannot express
    /// the distinction that matters:
    ///   <c>pii</c>     — the identifier VALUES (national ID, UNHCR number, passport). The re-identification
    ///                    risk. Only the roles that must legally verify a person against a document.
    ///   <c>contact</c> — phone and address. Reception and the call centre need these to reach the member;
    ///                    a lab technician confirming a specimen's owner does not.
    /// Name, member number and status stay in the baseline <c>identity</c> class: every reader needs them to
    /// know they have the right record at all, and withholding them would just push staff to guess.
    /// </summary>
    public static FieldAccessMatrix FieldMatrix() => new(new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["reception"] = Set(Classes.Contact, Classes.Coverage),          // reach the member; NOT the ID values
        ["call_center"] = Set(Classes.Contact),                           // 15.x verify-before-disclose sits on top
        ["beneficiary_mgmt"] = Set(Classes.Pii, Classes.Contact, Classes.Coverage), // registers against documents
        // 19.7 added the supervisor role to the issuer with patient:read, but this bundle was never
        // amended — so the role's searches returned silently-empty pages (the search endpoint DROPS rows
        // the engine denies rather than 403ing) and every by-id read was refused. A reviewer who approves
        // registrations must be able to read at least what the registrar could when creating them.
        ["beneficiary_mgmt_supervisor"] = Set(Classes.Pii, Classes.Contact, Classes.Coverage),
        ["case_manager"] = Set(Classes.Contact, Classes.Coverage, Classes.Diagnosis),
        ["doctor"] = Set(Classes.Pii, Classes.Contact, Classes.Diagnosis, Classes.Clinical, Classes.Prescription, Classes.Result, Classes.Coverage),
        ["nurse"] = Set(Classes.Contact, Classes.Clinical, Classes.Result, Classes.Coverage),
        ["medical_approval"] = Set(Classes.Diagnosis, Classes.Clinical, Classes.Result, Classes.Prescription),
        ["medical_director"] = Set(Classes.Pii, Classes.Diagnosis, Classes.Clinical, Classes.Result, Classes.Prescription, Classes.Financials),
        ["org_admin"] = Set(Classes.Coverage),                            // administers access, not PHI (§3.15)
        ["super_admin"] = Set(Classes.Coverage),                          // PHI only under break-glass (§3.16)
    });

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
}
