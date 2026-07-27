using Mersal.Auth;
using Microsoft.AspNetCore.Http;

namespace Mersal.Authz;

/// <summary>
/// The gate an OWNING service puts on a profile-seam endpoint (the <c>/for-beneficiary/…</c> and
/// <c>/profile-context</c> reads that phase 20's composition layer calls).
///
/// <para><b>Why the owning service consults the same matrix rather than getting its own rules.</b> The obvious
/// alternative was to add a <c>profile-read</c> rule to each service's policy bundle. It does not work: a
/// single <see cref="PolicyRule"/> carries ONE set of ABAC conditions, and this question has a different
/// condition per role — a doctor needs a treating relationship, a case manager needs an active assignment, and
/// reception needs neither because it only ever receives the meta variant. Expressing that as one rule would
/// have meant dropping the conditions, which is exactly how a seam becomes a way around the gate it sits
/// beside.</para>
///
/// <para><b>Why this is still two independent layers.</b> Both the owning service and profile-service consult
/// the same table, but each <i>resolves the facts itself, from data it owns</i>: emr knows the treating
/// relationship, case-service knows the assignment, orders knows the sensitivity and the grant. Neither layer
/// can stand in for the other — profile-service cannot read emr's treating table, and emr cannot see the
/// composed payload. What they share is the answer to "which roles may see this section", which SHOULD have
/// exactly one definition (design 39 §1).</para>
///
/// <para><b>It never widens anything.</b> The existing narrow rules — <c>orders:read</c> for a treating doctor,
/// <c>rx:read</c>, emr's treating/oversight split — are untouched and still guard every other endpoint. A seam
/// permits precisely the set the matrix already grants, and no more.</para>
/// </summary>
public static class ProfileSeam
{
    /// <summary>
    /// May this caller receive ANY of <paramref name="sections"/>, given the facts the owning service resolved?
    /// </summary>
    /// <returns><c>null</c> when allowed; a ready RFC-7807 403 otherwise.</returns>
    /// <remarks>
    /// "Any", not "all", because a seam commonly serves several sections at once (emr's profile-context serves
    /// past-medical-history AND encounters). A caller entitled to one of them is entitled to the call; the
    /// per-section shaping is then the profile's second layer.
    ///
    /// <para>A <see cref="ProfileSectionState.Restricted"/> cell does NOT open the door: existence-only means
    /// the caller learns the section exists, which the profile can say without fetching anything.</para>
    /// </remarks>
    public static IResult? Check(HbmpPrincipal? principal, ProfileContext context, params string[] sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (principal is null) return GateResults.Unauthenticated();
        ArgumentNullException.ThrowIfNull(context);

        foreach (var section in sections)
        {
            if (ProfilePolicies.Decide(section, context) is { State: ProfileSectionState.Visible })
                return null;
        }

        return GateResults.Forbidden(
            "urn:hbmp:profile-section-denied",
            detail: "Your role does not receive this section of the patient profile.",
            reason: ProfileReasons.RoleNotPermitted);
    }

    /// <summary>Build the evaluation context from the facts the OWNING service just resolved. Anything it does
    /// not own stays false — fail-closed, so an unresolved fact narrows the answer rather than widening it.</summary>
    public static ProfileContext ContextFor(
        HbmpPrincipal principal,
        bool treatingRelationship = false,
        bool caseAssignment = false,
        bool sensitiveGrantActive = false)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new ProfileContext
        {
            Roles = principal.Roles,
            TreatingRelationship = treatingRelationship,
            CaseAssignment = caseAssignment,
            SensitiveGrantActive = sensitiveGrantActive,
            ProviderId = principal.ProviderId,
        };
    }
}
