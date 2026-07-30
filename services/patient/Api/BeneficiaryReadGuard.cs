using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Patient.Domain;

namespace Mersal.Patient.Api;

/// <summary>
/// Phase 18.B3 (audit R2 S6) — the four things a beneficiary-directory read owes, in one place.
///
/// A read of this record was previously a bare <c>.RequireAuthorization()</c> returning the whole DTO. It now
/// (1) runs the authorization engine, so the decision is a policy rather than a routing accident, (2) checks
/// the row's tenant against the caller's, (3) emits a PHI-read audit event naming what was disclosed, and
/// (4) projects the record through <see cref="FieldProjector"/> so reception gets a phone number and not a
/// UNHCR registration number.
///
/// (3) and (4) are deliberately separate. The projector audits what it STRIPPED — a min-necessary denial — and
/// that is not the same record as "this person read this beneficiary", which §19 requires whether or not
/// anything was withheld. A read that strips nothing must still leave a trace.
/// </summary>
public sealed class BeneficiaryReadGuard(
    IHbmpPrincipalAccessor me,
    IAuthorizationEngine engine,
    FieldProjector projector,
    IAuditClient audit)
{
    /// <summary>Authorize a read of one beneficiary. Returns null when permitted; a ready 401/403 otherwise.
    /// The engine emits the audit for a Sensitive allow, so a permitted read is already on the record.</summary>
    public async Task<IResult?> AuthorizeAsync(Beneficiary b, CancellationToken ct)
    {
        var p = me.Principal;
        if (p is null) return GateResults.Unauthenticated();

        var decision = await engine.EvaluateAsync(
            new AuthzRequest(p, PatientPolicies.ReadBeneficiary,
                new ResourceRef
                {
                    Type = PatientPolicies.Resource,
                    Id = b.BeneficiaryId.ToString(),
                    TenantId = b.TenantId,
                },
                "TRT"),
            ct);

        return decision.IsAllowed
            ? null
            : GateResults.Forbidden("urn:hbmp:beneficiary-read-denied",
                detail: "You are not permitted to read this beneficiary record.", reason: decision.ReasonCode);
    }

    /// <summary>Project one beneficiary to the caller's readable field-classes and record the PHI read.
    /// <paramref name="context"/> distinguishes a targeted lookup from a search hit in the audit trail —
    /// one person opened deliberately is a different event from forty returned by a name query.</summary>
    public async Task<IReadOnlyDictionary<string, object?>> DiscloseAsync(
        Beneficiary b, string context, CancellationToken ct)
    {
        var p = me.Require();
        var projected = await projector.ProjectAsync(p, PatientPolicies.Resource, Fields(b), ct);

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = PatientPolicies.Resource,
            EntityId = b.BeneficiaryId.ToString(),
            Action = AuditAction.Read,
            ActorUserId = p.Subject,
            ActorRole = string.Join(',', p.Roles),
            TenantId = b.TenantId,
            DecisionOutcome = "disclosed",
            DecisionReasonCode = context,
            // What was actually handed over, not what was asked for.
            FieldClasses = [.. projected.Keys.Select(k => Fields(b)[k].FieldClass).Distinct().Order(StringComparer.Ordinal)],
        }, ct);

        return projected;
    }

    /// <summary>The record as (field → value + class). Identifier VALUES are <c>pii</c>; the identifier TYPES
    /// are not — knowing a member registered with a UNHCR number rather than a passport is operational, and
    /// hiding it would leave a caller unable to tell an empty list from a withheld one.</summary>
    private static Dictionary<string, (object? Value, string FieldClass)> Fields(Beneficiary b) => new(StringComparer.Ordinal)
    {
        ["beneficiaryId"] = (b.BeneficiaryId, DefaultPolicies.Classes.Identity),
        ["memberNo"] = (b.MemberNo, DefaultPolicies.Classes.Identity),
        // The card number identifies WHICH person is in front of you — the same job as the member number,
        // and the number a receptionist is actually holding. Identity, not pii: it is printed on a card the
        // beneficiary hands over, and withholding it would leave every desk unable to match the card to the
        // record while still showing them the name.
        ["cardNumber"] = (b.CardNumber, DefaultPolicies.Classes.Identity),
        ["givenName"] = (b.GivenName, DefaultPolicies.Classes.Identity),
        ["middleName"] = (b.MiddleName, DefaultPolicies.Classes.Identity),
        ["familyName"] = (b.FamilyName, DefaultPolicies.Classes.Identity),
        ["birthDate"] = (b.BirthDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            DefaultPolicies.Classes.Identity),
        // Travels WITH the date, always. A consumer that receives the date without the flag has no way to
        // know it is an estimate, which is precisely how an estimated date becomes a hard eligibility cutoff.
        ["birthDateIsApproximate"] = (b.BirthDateIsApproximate, DefaultPolicies.Classes.Identity),
        ["sex"] = (b.Sex, DefaultPolicies.Classes.Identity),
        ["nationalityCode"] = (b.NationalityCode, DefaultPolicies.Classes.Identity),
        // Programme references, not identity documents: they say which case file a person belongs to, which
        // is operational rather than a disclosure of legal status.
        ["individualNo"] = (b.IndividualNo, DefaultPolicies.Classes.Identity),
        ["caseNo"] = (b.CaseNo, DefaultPolicies.Classes.Identity),
        ["status"] = (b.Status.ToString(), DefaultPolicies.Classes.Identity),
        ["identifierTypes"] = (b.Identifiers.Where(i => !i.IsDeleted)
            .Select(i => i.IdentifierType.ToString()).ToArray(), DefaultPolicies.Classes.Identity),
        ["identifiers"] = (b.Identifiers.Where(i => !i.IsDeleted)
            .Select(i => new IdentifierDto(i.IdentifierType.ToString(), i.IdentifierValue, i.IsPrimary))
            .ToArray(), DefaultPolicies.Classes.Pii),
        ["contacts"] = (b.Contacts.Where(c => !c.IsDeleted)
            .Select(c => new { type = c.ContactType.ToString(), value = c.Value, isPrimary = c.IsPrimary })
            .ToArray(), DefaultPolicies.Classes.Contact),
    };
}
