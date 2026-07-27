using Mersal.Auth;
using Mersal.Authz;
using Mersal.Profile.Domain;

namespace Mersal.Profile.Infrastructure;

/// <summary>
/// Resolves the ABAC facts the section matrix evaluates, by ASKING THE SERVICE THAT OWNS EACH ONE.
///
/// <para>None of these facts is computed here, and that is deliberate. "Does this clinician treat this patient"
/// has exactly one answer, and it lives in emr; a second implementation in the aggregator would be a second
/// answer, and the two would disagree the first time a treating relationship expired. The profile asks and
/// obeys.</para>
///
/// <para>Fail-CLOSED: an unreachable owner yields <c>false</c>, so a fact that could not be established narrows
/// the profile rather than widening it.</para>
/// </summary>
public interface IProfileFactResolver
{
    Task<ProfileContext> ResolveAsync(
        HbmpPrincipal principal, Guid beneficiaryId, CallerCredentials caller, CancellationToken ct);
}

public sealed class HttpProfileFactResolver(CallerScopedHttp http) : IProfileFactResolver
{
    public async Task<ProfileContext> ResolveAsync(
        HbmpPrincipal principal, Guid beneficiaryId, CallerCredentials caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // Only ask for a fact some cell actually depends on. A pharmacy has no treating-relationship cell, so
        // asking emr about one is a wasted call AND a PHI-adjacent question nobody needed to answer.
        var needsTreating = principal.IsInRole("doctor") || principal.IsInRole("nurse");
        var needsAssignment = principal.IsInRole("case_manager");

        var treating = needsTreating && await TreatsAsync(beneficiaryId, caller, ct);
        var assigned = needsAssignment && await AssignedAsync(beneficiaryId, caller, ct);

        return new ProfileContext
        {
            Roles = principal.Roles,
            TreatingRelationship = treating,
            CaseAssignment = assigned,
            // The design-37 §6 grant is a PER-RESULT fact that orders-service applies as it serves each line.
            // The profile deliberately does not hold a beneficiary-wide "has a grant" flag: there is no such
            // thing, and inventing one would be exactly the shortcut around the gate design 39 §4 forbids.
            SensitiveGrantActive = false,
            ProviderId = principal.ProviderId,
        };
    }

    private async Task<bool> TreatsAsync(Guid beneficiaryId, CallerCredentials caller, CancellationToken ct)
    {
        try
        {
            using var doc = await http.GetAsync(
                "emr", $"/api/v1/treating-relationship?beneficiaryId={beneficiaryId}", caller, ct);
            return doc is not null && doc.RootElement.Bool("treats");
        }
#pragma warning disable CA1031 // fail-closed: an unanswerable fact narrows the profile
        catch (Exception) { return false; }
#pragma warning restore CA1031
    }

    private async Task<bool> AssignedAsync(Guid beneficiaryId, CallerCredentials caller, CancellationToken ct)
    {
        try
        {
            using var doc = await http.GetAsync(
                "case", $"/api/v1/cases/for-beneficiary/{beneficiaryId}", caller, ct);
            return doc is not null && doc.RootElement.Bool("assigned");
        }
#pragma warning disable CA1031
        catch (Exception) { return false; }
#pragma warning restore CA1031
    }
}

/// <summary>
/// The phase-15 caller-verification gate, CONSUMED rather than re-implemented (design 39 §4 cross-cutting).
///
/// <para>A call-centre principal reaching the profile must name the interaction they are on, and
/// callcentre-service must confirm a Passed verification for that interaction and that beneficiary. Without it
/// the profile returns 403 — the same answer the phase-15 member 360 has always given, from the same source of
/// truth.</para>
/// </summary>
public interface ICallVerificationGate
{
    Task<bool> IsVerifiedAsync(Guid interactionId, Guid beneficiaryId, CallerCredentials caller, CancellationToken ct);
}

public sealed class HttpCallVerificationGate(CallerScopedHttp http) : ICallVerificationGate
{
    public async Task<bool> IsVerifiedAsync(
        Guid interactionId, Guid beneficiaryId, CallerCredentials caller, CancellationToken ct)
    {
        try
        {
            using var doc = await http.GetAsync(
                "callcentre",
                $"/api/v1/call-interactions/{interactionId}/verification?beneficiaryId={beneficiaryId}",
                caller, ct);
            return doc is not null && doc.RootElement.Bool("verified");
        }
#pragma warning disable CA1031 // fail-closed: an unreachable verification service discloses nothing
        catch (Exception) { return false; }
#pragma warning restore CA1031
    }
}
