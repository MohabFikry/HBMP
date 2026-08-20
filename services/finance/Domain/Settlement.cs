using Mersal.Amounts;

namespace Mersal.Finance.Domain;

/// <summary>Settlement lifecycle. Draft → Submitted → Approved → Paid. Submit/approve are split for segregation of
/// duties (the approver must be a different principal than the submitter). No payment is executed here — Paid is a
/// recorded outcome, not a money movement (10.2 "no payment execution").</summary>
public enum SettlementStatus { Draft, Submitted, Approved, Paid }

/// <summary>A provider settlement for a period: the priced roll-up of delivered services. Prices come from the
/// provider-service <c>provider_contract</c> / <c>contract_service_line</c> agreed prices (22 §5.3) — READ, never
/// owned or mutated here.</summary>
public sealed class Settlement
{
    public Guid SettlementId { get; set; }
    public string SettlementNo { get; set; } = default!;    // STL-YYYY-NNNNNN
    public string TenantId { get; set; } = default!;
    public Guid ProviderId { get; set; }
    public Guid? ContractId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public string CurrencyCode { get; set; } = "EGP";

    /// <summary>
    /// The settlement's currency, TYPED (ADR-0043).
    ///
    /// <para>Every amount on this settlement and its lines is in this currency — the lines carry no currency
    /// of their own, deliberately: a line denominated differently from the settlement it belongs to is not a
    /// thing anyone wants to be able to represent. Arithmetic goes through <see cref="Money"/> in this
    /// currency; the columns stay <c>numeric</c>.</para>
    ///
    /// <para>Throws on a code the platform does not settle in, at the point of reading. That is the whole
    /// value of the property: the alternative is an amount that quietly becomes EGP.</para>
    /// </summary>
    public Currency Currency => Currencies.Parse(CurrencyCode);

    public decimal Total { get; set; }
    public SettlementStatus Status { get; set; } = SettlementStatus.Draft;
    public string? SubmittedBy { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint RowVersion { get; set; }                    // xmin — optimistic concurrency (racing approvers)
    public List<SettlementLine> Lines { get; set; } = [];
}

/// <summary>A priced settlement line — a billing service code, delivered quantity, the agreed unit price READ from
/// the provider contract, and the line total. No clinical field.</summary>
public sealed class SettlementLine
{
    public Guid SettlementLineId { get; set; }
    public string TenantId { get; set; } = "";              // RLS tenant scope (ADR-0011)
    public Guid SettlementId { get; set; }
    public string ServiceCode { get; set; } = default!;     // billing code (CPT/LOINC/ATC)
    public string ServiceLine { get; set; } = "General";
    public int DeliveredQty { get; set; }
    public decimal AgreedUnitPrice { get; set; }            // from provider_contract (read, not owned)
    public decimal LineTotal { get; set; }

    /// <summary>This line's unit price as money, in the SETTLEMENT's currency — the line has none of its own
    /// (see <see cref="Settlement.Currency"/>). Pass the parent's; there is no other correct answer.</summary>
    public Money UnitPriceIn(Currency currency) => new(AgreedUnitPrice, currency);

    /// <summary>This line's total as money. Recomputed from unit × quantity rather than read from the stored
    /// column, so a caller comparing the two is comparing something to something.</summary>
    public Money TotalIn(Currency currency) => UnitPriceIn(currency) * DeliveredQty;

    /// <summary>Where <see cref="AgreedUnitPrice"/> came from. See <see cref="SettlementPriceSource"/> —
    /// a line the contract does not price is not the same kind of number as one it does, and a reviewer
    /// issuing the draft has to be able to tell them apart.</summary>
    public string PriceSource { get; set; } = SettlementPriceSource.Contract;
}

/// <summary>
/// How a settlement line was priced.
///
/// <para>The distinction the 2026-08-09 audit asked for. An unpriced code used to be settled at the
/// provider's own observed AVERAGE unit cost with nothing recording that it had been — contrary to the rule
/// claims and reimbursement already apply, that the absence of a tariff is not permission to pay anything,
/// and using the statistic a single mispriced small delivery moves most.</para>
/// </summary>
public static class SettlementPriceSource
{
    /// <summary>The provider's agreed price book named this code.</summary>
    public const string Contract = "Contract";

    /// <summary>It did not. The line is priced at the LOWEST unit cost observed for the code in the period —
    /// a floor, pending a tariff, which can only under-state. That direction is chosen deliberately: a
    /// provider queries an underpayment, and nobody queries an overpayment.</summary>
    public const string ObservedFloor = "ObservedFloor";
}

/// <summary>Business-key formatter for settlements (0A §3). STL-YYYY-NNNNNN — consistent with the platform's other
/// per-year business keys.</summary>
public static class SettlementNo
{
    public static string Format(int year, int sequence) => $"STL-{year:D4}-{sequence:D6}";
}
