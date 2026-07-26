using Mersal.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Claims.Infrastructure;

/// <summary>Clinical-free external facts for adjudicating one line — gathered from eligibility/policy/approvals/
/// provider at the boundary. Coverage-limit facts are READ (limit − consumed); the claims path never writes them.</summary>
public sealed record ExternalFacts
{
    public bool BeneficiaryEligible { get; init; } = true;
    public bool PolicyValid { get; init; } = true;
    public bool CoverageCategoryMatches { get; init; } = true;
    public bool IsGatedService { get; init; }
    public AuthorizationState Authorization { get; init; } = AuthorizationState.None;
    public decimal? AuthorizedScopeAmount { get; init; }
    public bool ProviderInNetwork { get; init; } = true;
    public bool ContractEffective { get; init; } = true;
    public decimal? LimitRemaining { get; init; }
    public decimal MemberShare { get; init; }
}

/// <summary>Source of the external adjudication facts. The HTTP-backed implementation (eligibility/policy/approvals/
/// provider) is wired later; a permissive default keeps the engine runnable — the gated-auth / limit / network rules
/// are proven directly on the pure <see cref="Adjudicator"/> in the unit matrix.</summary>
public interface IExternalAdjudicationFacts
{
    Task<ExternalFacts> GetAsync(Claim claim, ClaimLine line, string? bearer, CancellationToken ct = default);
}

/// <summary>Default: all-clear, ungated, unlimited, zero co-pay. If the line already carries an authorization id, it
/// is treated as an Approved gated service so the linkage check is satisfied rather than spuriously blocking.</summary>
public sealed class PermissiveAdjudicationFacts : IExternalAdjudicationFacts
{
    public Task<ExternalFacts> GetAsync(Claim claim, ClaimLine line, string? bearer, CancellationToken ct = default) =>
        Task.FromResult(new ExternalFacts
        {
            IsGatedService = line.AuthorizationId is not null,
            Authorization = line.AuthorizationId is not null ? AuthorizationState.Approved : AuthorizationState.None,
        });
}

public sealed record AdjudicatedLine(Guid ClaimLineId, AdjudicationResult Result);

/// <summary>Runs pre-adjudication (10b.3) over every line of a claim, in the fixed 9-step order collecting ALL reason
/// codes, and persists the per-line output (system_recommendation, reason_codes, allowed_amount, member_share,
/// rule_version). The line status stays Pending — the officer decides (10b.4). The claim moves to UnderAdjudication.
/// Re-running is idempotent in effect: it recomputes the output; the append-only per-run history is the audit event.
/// It NEVER writes a coverage accumulator (the claims schema has no such column).</summary>
public sealed class AdjudicationService(ClaimsDbContext db, IExternalAdjudicationFacts facts)
{
    public async Task<IReadOnlyList<AdjudicatedLine>?> AdjudicateAsync(
        string tenantId, Guid claimId, string? bearer, CancellationToken ct = default)
    {
        var claim = await db.Claims.Include(c => c.Lines).FirstOrDefaultAsync(c => c.ClaimId == claimId && c.TenantId == tenantId, ct);
        if (claim is null) return null;

        var results = new List<AdjudicatedLine>();
        decimal priced = 0m;
        foreach (var line in claim.Lines.Where(l => l.Status != ClaimLineStatus.Void))
        {
            var ext = await facts.GetAsync(claim, line, bearer, ct);
            var duplicate = line.FulfillmentRef is { } fref && await db.ClaimLines.AsNoTracking()
                .AnyAsync(l => l.FulfillmentRef == fref && l.ClaimLineId != line.ClaimLineId && l.Status != ClaimLineStatus.Void, ct);

            var f = new AdjudicationFacts
            {
                BilledAmount = line.BilledAmount,
                ContractPrice = line.ContractPrice,
                BeneficiaryEligible = ext.BeneficiaryEligible,
                PolicyValid = ext.PolicyValid,
                CoverageCategoryMatches = ext.CoverageCategoryMatches,
                IsGatedService = ext.IsGatedService,
                Authorization = ext.Authorization,
                AuthorizedScopeAmount = ext.AuthorizedScopeAmount,
                HasFulfillmentRecord = line.FulfillmentRef is not null,
                IsDuplicate = duplicate,
                ProviderInNetwork = ext.ProviderInNetwork,
                ContractEffective = ext.ContractEffective,
                LimitRemaining = ext.LimitRemaining,
                MemberShare = ext.MemberShare,
            };
            var r = Adjudicator.Evaluate(f);

            line.SystemRecommendation = r.Recommendation;
            line.ReasonCodes = [.. r.ReasonCodes];
            line.AllowedAmount = r.AllowedAmount;
            line.MemberShare = r.MemberShare;
            line.RuleVersion = r.RuleVersion;
            priced += line.ContractPrice ?? 0m;
            results.Add(new AdjudicatedLine(line.ClaimLineId, r));
        }

        claim.PricedAmount = priced;
        if (claim.Status is ClaimStatus.Draft or ClaimStatus.Submitted)
            claim.Status = ClaimStatus.UnderAdjudication;

        await db.SaveChangesAsync(ct);
        return results;
    }
}
