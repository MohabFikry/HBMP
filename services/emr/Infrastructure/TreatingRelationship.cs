using Microsoft.EntityFrameworkCore;

namespace Mersal.Emr.Infrastructure;

/// <summary>Answers the core EMR access question (US-030): does the acting clinician have a TREATING
/// relationship with a beneficiary? This is the <b>row-level</b> half of the treating-relationship rule — the
/// authorization engine's ABAC condition is the policy half; both must agree. A relationship exists when the
/// clinician owns (authored) or is the provider on an encounter for that beneficiary. The result feeds
/// <c>ResourceRef.TreatingBeneficiaryIds</c> so the engine can enforce it.</summary>
public interface ITreatingRelationship
{
    Task<bool> TreatsAsync(string actorSubject, string? actorProviderId, Guid beneficiaryId, CancellationToken ct = default);
}

public sealed class TreatingRelationship(EmrDbContext db) : ITreatingRelationship
{
    public async Task<bool> TreatsAsync(string actorSubject, string? actorProviderId, Guid beneficiaryId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorSubject)) return false;
        Guid? providerGuid = Guid.TryParse(actorProviderId, out var g) ? g : null;

        // Treating = the clinician started/owns an encounter for this beneficiary (encounter.created_by), OR is
        // the provider on one (encounter.provider_id) — i.e. assigned via encounter (US-030 "encounter provider_id").
        return await db.Encounters.AsNoTracking().AnyAsync(e =>
            e.BeneficiaryId == beneficiaryId
            && (e.CreatedBy == actorSubject || (providerGuid != null && e.ProviderId == providerGuid)), ct);
    }
}
