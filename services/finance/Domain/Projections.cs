namespace Mersal.Finance.Domain;

// ── Finance projection DTOs. EVERY type here implements IFinanceProjection and is checked by FinanceProjection.Guard
// (a unit test guards all of them). Names are chosen to avoid clinical tokens — there is no property that can carry
// a diagnosis / note / result. Masked-min PII only (a beneficiary appears as a masked reference, never a name).

/// <summary>A utilization row — authorized-vs-delivered quantities + spend for a billing service code.</summary>
public sealed record UtilizationRow(
    string ServiceCode,
    string ServiceLine,
    string CoverageCategory,
    string? ProviderRef,
    int AuthorizedQty,
    int DeliveredQty,
    decimal Spend) : IFinanceProjection;

/// <summary>The utilization aggregate response — rows + totals. No clinical filter or column exists.</summary>
public sealed record UtilizationView(
    IReadOnlyList<UtilizationRow> Rows,
    int TotalAuthorized,
    int TotalDelivered,
    decimal TotalSpend) : IFinanceProjection
{
    public static UtilizationView From(IReadOnlyList<UtilizationRow> rows) =>
        new(rows, rows.Sum(r => r.AuthorizedQty), rows.Sum(r => r.DeliveredQty), rows.Sum(r => r.Spend));
}

/// <summary>A financial summary bucket — spend + delivered quantity by billing dimension (service line / category /
/// provider). Donor/leadership roll-up. No diagnosis.</summary>
public sealed record SummaryBucket(string Dimension, string Key, int DeliveredQty, decimal Spend) : IFinanceProjection;

public sealed record FinancialSummaryView(
    string Dimension,
    IReadOnlyList<SummaryBucket> Buckets,
    decimal TotalSpend) : IFinanceProjection;

/// <summary>A settlement line view — billing code, delivered qty, agreed price, line total.</summary>
public sealed record SettlementLineView(
    string ServiceCode,
    string ServiceLine,
    int DeliveredQty,
    decimal AgreedUnitPrice,
    decimal LineTotal,
    /// <summary>"Contract" or "ObservedFloor" — whether a tariff priced this line. Projected rather than
    /// kept internal: it is the question a reviewer has to answer before issuing the draft.</summary>
    string PriceSource) : IFinanceProjection
{
    public static SettlementLineView From(SettlementLine l) =>
        new(l.ServiceCode, l.ServiceLine, l.DeliveredQty, l.AgreedUnitPrice, l.LineTotal, l.PriceSource);
}

/// <summary>A settlement view — header + priced lines. Provider is a reference; no beneficiary PII, no clinical.</summary>
public sealed record SettlementView(
    Guid SettlementId,
    string SettlementNo,
    string ProviderRef,
    Guid? ContractId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string CurrencyCode,
    decimal Total,
    string Status,
    IReadOnlyList<SettlementLineView> Lines,
    /// <summary>
    /// Who submitted this settlement, and who approved it. Staff subject ids — the same class of identifier
    /// the approvals worklist carries as <c>AssignedReviewerId</c>, and no more identifying than that.
    /// </summary>
    /// <remarks>
    /// <para>Projected so segregation of duties can be honoured BEFORE the click rather than only in the
    /// refusal. The approve handler compares <c>SubmittedBy</c> against the calling principal and answers 409
    /// <c>urn:hbmp:sod-violation</c> when they match; without these fields a screen has no way to know that in
    /// advance, so it offers the submitter an Approve button and then refuses it. A control that is working
    /// correctly reads as a defect when the only way to discover the rule is to break it.</para>
    /// <para>The refusal stays. The client is not the authority on who may release a payment — it is merely
    /// no longer the only place the rule becomes visible.</para>
    /// </remarks>
    string? SubmittedBy = null,
    string? ApprovedBy = null) : IFinanceProjection
{
    public static SettlementView From(Settlement s) =>
        new(s.SettlementId, s.SettlementNo, s.ProviderId.ToString(), s.ContractId, s.PeriodStart, s.PeriodEnd,
            s.CurrencyCode, s.Total, s.Status.ToString(), s.Lines.Select(SettlementLineView.From).ToList(),
            s.SubmittedBy, s.ApprovedBy);
}
