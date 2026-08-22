namespace Mersal.Policy.Domain;

// Phase 19.1 — the PAS product layer (design 38 §3). payer → plan → effective-dated plan_version →
// benefit_rule. Cross-service ids stay logical values; the only FKs are inside the policy schema.

public enum PayerType { SelfFunded, Donor, Government, PartnerNGO, Insurer }
public enum CatalogStatus { Active, Inactive }

/// <summary>How often the payer is billed. 19.7 — a number of days answers "by when"; this answers "how
/// often", and the two are independent (net-30 on a monthly invoice is not net-30 per claim).</summary>
public enum PayerInvoicingCadence { OnClaim, Monthly, Quarterly, SemiAnnual, Annual }

/// <summary>Lifecycle of a benefit configuration. <c>Draft</c> is freely editable; <c>Active</c> is in force and
/// IMMUTABLE; <c>Superseded</c> was replaced by a later version but still resolves for service dates inside its
/// own window; <c>Retired</c> was withdrawn without a successor and likewise still resolves for the past.</summary>
public enum PlanVersionStatus { Draft, Active, Superseded, Retired }

/// <summary>
/// The counterparty a policy is funded BY — a donor grant, a government programme, a partner NGO, an insurer,
/// or Mersal's own funds. <see cref="Policy.PayerId"/> points here, utilization rolls up to here (19.4), and a
/// user can be RESTRICTED to one (19.5), which makes this the top of the commercial hierarchy.
///
/// <para>19.7 gave it the facts that decide whether it can actually pay. Before that the row could label a
/// payer and not administer one: the agreement window, the funding ceiling, the settlement terms and the
/// people to call all lived outside the platform, and <see cref="Contact"/> — reserved since 0005 — was
/// <c>{}</c> on every row ever created because nothing read or wrote it.</para>
/// </summary>
public sealed class Payer
{
    public Guid PayerId { get; set; }
    public string TenantId { get; set; } = "";
    public string PayerCode { get; set; } = default!;
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;
    public PayerType PayerType { get; set; }

    /// <summary>The reference the PAYER knows this agreement by — a donor's grant number, an insurer's
    /// licence. Reconciliation happens against their reference, not ours.</summary>
    public string? ExternalRef { get; set; }
    public string? AgreementNo { get; set; }

    /// <summary>The funding window, half-open like every other window in this schema: <c>[from, to)</c>.
    /// Deliberately NOT the same thing as <see cref="Status"/> — see <see cref="AgreementState"/>.</summary>
    public DateOnly? AgreementFrom { get; set; }
    public DateOnly? AgreementTo { get; set; }

    /// <summary>What the payer has committed, in <see cref="Currency"/>. Null = uncapped (Mersal's own funds
    /// usually are). Zero is refused at the database: a ceiling of nothing is not "uncapped", it is a payer
    /// that would refuse every claim for a reason no screen explains.</summary>
    public decimal? FundingCeiling { get; set; }
    public string Currency { get; set; } = "EGP";

    /// <summary>Days from invoice to payment — "net 30" as the number every signed contract states it as.</summary>
    public int? SettlementTermsDays { get; set; }
    public PayerInvoicingCadence? InvoicingCadence { get; set; }
    /// <summary>Days after the service date within which a claim must reach this payer. Past it the money is
    /// gone regardless of whether the care was covered, which is why it belongs on the payer and not in a
    /// finance spreadsheet.</summary>
    public int? ClaimSubmissionWindowDays { get; set; }

    public string? Notes { get; set; }

    /// <summary>Operational contact detail (jsonb), shaped by <see cref="PayerContacts"/>. Never beneficiary
    /// PII — these are the payer's own staff.</summary>
    public string Contact { get; set; } = "{}";

    public CatalogStatus Status { get; set; } = CatalogStatus.Active;
    /// <summary>Why the status is what it is. A deactivation recorded without one is a record of the fact and
    /// none of the decision, and the reason is the half somebody needs six months later.</summary>
    public string? StatusReason { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }
    public Guid? StatusChangedBy { get; set; }

    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public string? UpdatedByName { get; set; }

    /// <summary>
    /// Where the AGREEMENT stands on a date, which is not where the RECORD stands.
    ///
    /// <para>Collapsing the two would lose the difference between "the grant ran its course" and "we stopped
    /// working with them". An Active payer whose window has closed is a real and actionable combination —
    /// somebody has to renew it or stop enrolling against it — so it is surfaced rather than hidden.</para>
    /// </summary>
    public PayerAgreementState AgreementState(DateOnly on)
    {
        if (AgreementFrom is null && AgreementTo is null) return PayerAgreementState.Unrecorded;
        if (AgreementFrom is { } from && on < from) return PayerAgreementState.NotYetStarted;
        if (AgreementTo is { } to && on >= to) return PayerAgreementState.Expired;
        return PayerAgreementState.InForce;
    }
}

/// <summary>Where a payer's funding agreement stands on a date. <c>Unrecorded</c> is its own answer and not a
/// synonym for in-force: "we never wrote the window down" and "the window is open" are different problems.</summary>
public enum PayerAgreementState { Unrecorded, NotYetStarted, InForce, Expired }

/// <summary>
/// The typed shape of <see cref="Payer.Contact"/>.
///
/// <para>Three NAMED roles rather than a list, because the three questions asked of a payer are different
/// questions asked of different people: the day-to-day counterpart, the one who settles invoices, and the one
/// you escalate a stalled claim to. A flat list of contacts makes an operator guess which is which, and the
/// guess is made at the moment they most need to be right.</para>
/// </summary>
public sealed record PayerContacts(
    PayerContact? Primary = null,
    PayerContact? Finance = null,
    PayerContact? Escalation = null);

public sealed record PayerContact(string? Name, string? Title, string? Email, string? Phone)
{
    /// <summary>An entry with nothing in it is not a contact. Stored as null rather than as four empty
    /// strings, so "no finance contact" reads as absent instead of as present-and-blank.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Title)
        && string.IsNullOrWhiteSpace(Email) && string.IsNullOrWhiteSpace(Phone);
}

/// <summary>One row of <c>policy.payer_history</c> — the snapshot the 0020 trigger writes on every insert and
/// update. Read at the same authority that maintains the payer; the hash-chained audit trail answers the
/// compliance question separately (see 0020's header).</summary>
public sealed class PayerHistoryEntry
{
    public long HistoryId { get; set; }
    public Guid PayerId { get; set; }
    public string TenantId { get; set; } = "";
    public string Operation { get; set; } = default!;
    public string RowSnapshot { get; set; } = "{}";
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>
/// The reusable benefit PRODUCT. A plan is a name and a category; what it actually covers lives in its
/// effective-dated <see cref="PlanVersion"/>s, which is why this row is small and its versions are not.
///
/// <para>19.8 gave it the same treatment 19.7 gave the payer: it could be created and then never corrected
/// or withdrawn. <see cref="Status"/> has accepted <c>Inactive</c> since 0005 and no code path ever wrote
/// it, so a plan withdrawn from sale was indistinguishable from one still being enrolled onto.</para>
/// </summary>
public sealed class Plan
{
    public Guid PlanId { get; set; }
    public string TenantId { get; set; } = "";
    public string PlanCode { get; set; } = default!;
    public string NameEn { get; set; } = default!;
    public string NameAr { get; set; } = default!;
    public string? Description { get; set; }
    public string Category { get; set; } = default!;
    public CatalogStatus Status { get; set; } = CatalogStatus.Active;
    /// <summary>Why the status is what it is. Required on every status change — see 0021's header.</summary>
    public string? StatusReason { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }
    public Guid? StatusChangedBy { get; set; }
    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public string? UpdatedByName { get; set; }
}

/// <summary>One row of <c>policy.plan_history</c> — the snapshot 0021's trigger writes on every insert and
/// update, read at the same authority that maintains the plan.</summary>
public sealed class PlanHistoryEntry
{
    public long HistoryId { get; set; }
    public Guid PlanId { get; set; }
    public string TenantId { get; set; } = "";
    public string Operation { get; set; } = default!;
    public string RowSnapshot { get; set; } = "{}";
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>An effective-dated, immutable-once-active snapshot of a plan's benefit configuration. Everything
/// downstream — eligibility, authorization, claims — resolves the version in force on the SERVICE DATE
/// (design 38 §7.1), so this type is what makes retrospective adjudication correct.</summary>
public sealed class PlanVersion
{
    public Guid PlanVersionId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid PlanId { get; set; }
    public int VersionNo { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    /// <summary>EXCLUSIVE end of the window; null = open-ended. A successor starts on exactly this date.</summary>
    public DateOnly? EffectiveTo { get; set; }
    public PlanVersionStatus Status { get; set; } = PlanVersionStatus.Draft;
    public Guid? ActivatedBy { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public Guid? SupersededByVersionId { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    public List<BenefitRule> Rules { get; set; } = [];

    /// <summary>True when this version's benefit configuration may still be edited.</summary>
    public bool IsEditable => Status == PlanVersionStatus.Draft;

    /// <summary>Half-open containment per design 38 §7.1: <c>[effective_from, effective_to)</c>. The start day is
    /// in force; the end day belongs to the successor.</summary>
    public bool Covers(DateOnly serviceDate) =>
        serviceDate >= EffectiveFrom && (EffectiveTo is null || serviceDate < EffectiveTo.Value);
}

/// <summary>Per-benefit-category configuration inside a plan version. This is the row that a member's
/// coverage + coverage_limit are GENERATED from at enrolment (19.2), which is what makes an entitlement
/// explainable back to a specific version.
///
/// <para>19.1b moved COST SHARE off this type onto <see cref="BenefitRuleTier"/>. What is left here are the
/// properties of the benefit itself — whether it is covered, how much of it, how long the member waits, what
/// is excluded. What the member PAYS depends on where the care was delivered, so it belongs per tier.</para>
/// </summary>
public sealed class BenefitRule
{
    public Guid RuleId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid PlanVersionId { get; set; }
    public Guid BenefitCategoryId { get; set; }
    public bool IsCovered { get; set; } = true;
    public LimitType? LimitType { get; set; }
    public decimal? LimitValue { get; set; }
    public ResetPeriod ResetPeriod { get; set; } = ResetPeriod.None;
    public decimal? Deductible { get; set; }

    /// <summary>The plan's deductible does not apply to this category (primary care commonly waives it).
    /// Deliberately NOT modelled as a zero deductible: "this category is exempt" and "this plan has no
    /// deductible" survive a plan amendment differently, and only the exemption should follow the category.</summary>
    public bool DeductibleWaived { get; set; }

    public int WaitingPeriodDays { get; set; }
    /// <summary>The plan-level default. A tier may override it via
    /// <see cref="BenefitRuleTier.RequiresPreauthOverride"/> — out-of-network care commonly needs
    /// authorization for a service that is open-access in-network.</summary>
    public bool RequiresPreauth { get; set; }
    public decimal? PreauthCostThreshold { get; set; }
    /// <summary>Coded exclusions (jsonb array of codes).</summary>
    public string Exclusions { get; set; } = "[]";
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>The cost-share grid: one row per network tier. Activation requires every Active tier to be
    /// present (19.1b) — an unconfigured tier is a validation error, never a silent default.</summary>
    public List<BenefitRuleTier> Tiers { get; set; } = [];
}

/// <summary>
/// What a member pays for one benefit category AT ONE NETWORK TIER (design 38 §3, phase 19.1b).
///
/// This is what makes "in-network 10%, out-of-network 40% or not covered" expressible. The tier itself is
/// owned by provider-service (network administration); policy administration only decides the price at it —
/// which is why <see cref="NetworkTierId"/> is a plain value rather than a foreign key.
/// </summary>
public sealed class BenefitRuleTier
{
    public Guid RuleTierId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid BenefitRuleId { get; set; }

    /// <summary>provider.network_tier — a cross-service VALUE, validated at write time (no cross-schema FK).</summary>
    public Guid NetworkTierId { get; set; }

    /// <summary>The tier's code, snapshotted at authoring time. A plan version is immutable and stays
    /// resolvable for as long as a claim can reference it, so reading a years-old version must not depend on a
    /// live call into another service.</summary>
    public string TierCode { get; set; } = default!;

    /// <summary>An explicit "not covered at this tier" — a real statement (an HMO paying nothing
    /// out-of-network), and deliberately NOT the same as the tier being absent, which activation rejects.</summary>
    public bool IsCovered { get; set; } = true;

    public decimal? CopayFixed { get; set; }
    public decimal? CopayPercent { get; set; }
    public decimal? CoinsurancePercent { get; set; }

    /// <summary>The co-pay paid here accrues toward the member's deductible for LATER services. It does not
    /// change what they pay today; it changes what they pay next, which is why it is explicit rather than
    /// assumed. The accumulator that consumes it arrives with member-level accumulators (19.2).</summary>
    public bool CopayCountsTowardDeductible { get; set; }

    /// <summary>Overrides <see cref="BenefitRule.RequiresPreauth"/> for this tier; null = inherit.</summary>
    public bool? RequiresPreauthOverride { get; set; }

    /// <summary>Scales the rule's limit at this tier (0.5 = half the ceiling out-of-network); null = inherit.</summary>
    public decimal? LimitMultiplier { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    /// <summary>Whether pre-authorization is required here, resolving the override against the rule default.</summary>
    public bool ResolvesPreauth(BenefitRule rule) =>
        RequiresPreauthOverride ?? (rule ?? throw new ArgumentNullException(nameof(rule))).RequiresPreauth;

    /// <summary>The limit that applies at this tier, resolving the multiplier against the rule's own limit.
    /// Null stays null — an unlimited benefit is not made finite by a tier multiplier.
    ///
    /// <para><b>Returns <c>decimal</c> and not <c>Money</c>, deliberately (ADR-0043).</b> A limit is only
    /// SOMETIMES an amount: <c>LimitType.Count</c> means it is a number of sessions, and "three
    /// physiotherapy visits" typed as <c>Money.Egp(3)</c> would be three pounds of physiotherapy. The type
    /// cannot be chosen per-row, so it is chosen for the honest case. The rounding is still the platform's
    /// one mode, which is what the disagreement here actually was.</para></summary>
    public decimal? ResolvesLimit(BenefitRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.LimitValue is not { } limit) return null;
        // BANKER'S, matching Mersal.Money. This is an amount in EGP at the platform's 2dp settlement scale,
        // and it used to round half AWAY FROM ZERO — so a tier limit landing on a half-piastre came out a
        // piastre higher here than the same figure does anywhere claims or eligibility computes it. See the
        // rule in libs/money/Tests: at Money.Scale there is one rounding mode.
        return LimitMultiplier is { } m ? decimal.Round(limit * m, 2, MidpointRounding.ToEven) : limit;
    }
}

/// <summary>A tier as policy administration needs to know it: an id and a code. Deliberately NOT a copy of
/// provider-service's entity — policy-service consumes the catalogue, it does not model the network.</summary>
public sealed record NetworkTierRef(Guid NetworkTierId, string TierCode);
