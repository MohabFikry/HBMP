using System.Text.Json;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Api;

// Phase 19.7 — the payer as an administrable record rather than a label (design 56).
//
// The wire shapes are SPLIT along the line the reader is allowed to cross, not along the line the table is
// laid out on. `PayerAgreementView` says whether this funding is still running, which is an operational fact
// anyone administering the book needs; `PayerFinancialTermsView` says how much money and on what terms, which
// is commercial and follows `AdministrativeProjection.MayReadContract` — the same rule the policy and member
// query surfaces already apply to `payerId` and `maxMembers`. A single flat DTO would have forced one
// decision for both, and the one that fits everybody is the wrong one twice.

/// <summary>What the payer's own paperwork calls this arrangement, and when it runs. No amounts — see
/// <see cref="PayerFinancialTermsView"/> for the half that is commercially sensitive.</summary>
public sealed record PayerAgreementView(
    string? ExternalRef,
    string? AgreementNo,
    DateOnly? AgreementFrom,
    DateOnly? AgreementTo,
    /// <summary>Where the AGREEMENT stands today — <c>Unrecorded | NotYetStarted | InForce | Expired</c>.
    /// Projected rather than left for the client to derive: an Active payer whose window closed last month is
    /// the combination somebody has to act on, and two implementations of "is it still running" is how one
    /// screen shows it and another does not.</summary>
    string State);

/// <summary>Money and terms. Withheld entirely (null) from a caller who may not read contract terms, rather
/// than nulled field by field — a block of nulls reads as "not recorded", which is a different and wrong
/// answer to give somebody about a funding ceiling that exists.</summary>
public sealed record PayerFinancialTermsView(
    decimal? FundingCeiling,
    string Currency,
    int? SettlementTermsDays,
    string? InvoicingCadence,
    int? ClaimSubmissionWindowDays);

/// <summary>The payer's book of business — how much of the platform hangs off this counterparty. Counts
/// always; amounts only for a caller entitled to them (<c>MayReadAmounts</c>), which is the same split the
/// policy query surface makes.</summary>
public sealed record PayerBookView(
    int PolicyCount,
    int ActivePolicyCount,
    int MemberCount,
    int ActiveMemberCount,
    int PlanCount,
    /// <summary>Sum of the coverage limits generated under this payer's policies — what has been COMMITTED,
    /// which is the number the funding ceiling is meant to be compared against.</summary>
    decimal? CommittedLimit,
    decimal? ConsumedValue,
    /// <summary>Committed as a percentage of the funding ceiling, or null when either is unknown. The
    /// PERCENTAGE survives a caller who may not see the amounts, exactly as it does on the policy query:
    /// "this grant is 92% committed" is operational, the pounds are commercial.</summary>
    decimal? CeilingPercentCommitted);

public sealed record PayerView(
    Guid PayerId, string PayerCode, string NameEn, string NameAr, string PayerType, string Status,
    string? StatusReason, DateTimeOffset? StatusChangedAt,
    PayerAgreementView Agreement,
    PayerFinancialTermsView? Terms,
    PayerContacts? Contacts,
    string? Notes,
    DateTimeOffset UpdatedAt, string? UpdatedByName)
{
    public static PayerView From(Payer p, DateOnly today, bool mayReadContract)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(
            p.PayerId, p.PayerCode, p.NameEn, p.NameAr, p.PayerType.ToString(), p.Status.ToString(),
            p.StatusReason, p.StatusChangedAt,
            new PayerAgreementView(p.ExternalRef, p.AgreementNo, p.AgreementFrom, p.AgreementTo,
                p.AgreementState(today).ToString()),
            mayReadContract
                ? new PayerFinancialTermsView(p.FundingCeiling, p.Currency, p.SettlementTermsDays,
                    p.InvoicingCadence?.ToString(), p.ClaimSubmissionWindowDays)
                : null,
            PayerContactCodec.Read(p.Contact),
            p.Notes,
            p.UpdatedAt, p.UpdatedByName);
    }
}

/// <summary>One payer plus its book of business. What <c>GET /payers/{id}</c> answers, because "who is this
/// payer" and "what is riding on them" are never asked separately — the second is why the first was opened.</summary>
public sealed record PayerDetailView(PayerView Payer, PayerBookView Book);

/// <summary>One row of the payer's own change history (0020's trigger). The snapshot is read through the
/// shared <see cref="SnapshotJson"/> reader (19.8) and projected into named
/// fields rather than handed over raw: the raw row carries the tenant id and the internal signature columns,
/// and a history view is not a place to leak the shape of the table.</summary>
public sealed record PayerHistoryEntryView(
    long HistoryId, string Operation, DateTimeOffset RecordedAt,
    string? ActorName, Guid? ActorId,
    string NameEn, string NameAr, string PayerType, string Status, string? StatusReason,
    string? AgreementNo, DateOnly? AgreementFrom, DateOnly? AgreementTo,
    decimal? FundingCeiling, string? Currency, int? SettlementTermsDays,
    string? InvoicingCadence, int? ClaimSubmissionWindowDays)
{
    public static PayerHistoryEntryView From(PayerHistoryEntry e, bool mayReadContract)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var doc = SnapshotJson.Parse(e.RowSnapshot);
        var r = doc?.RootElement;
        return new(
            e.HistoryId, e.Operation, e.RecordedAt,
            SnapshotJson.Str(r, "updated_by_name"), SnapshotJson.Uuid(r, "updated_by"),
            SnapshotJson.Str(r, "name_en") ?? "", SnapshotJson.Str(r, "name_ar") ?? "",
            SnapshotJson.Str(r, "payer_type") ?? "", SnapshotJson.Str(r, "status") ?? "", SnapshotJson.Str(r, "status_reason"),
            SnapshotJson.Str(r, "agreement_no"), SnapshotJson.Date(r, "agreement_from"), SnapshotJson.Date(r, "agreement_to"),
            // The commercial half of a history row follows the same rule as the commercial half of the payer.
            // A reader who may not see today's ceiling must not be able to read yesterday's.
            mayReadContract ? SnapshotJson.Dec(r, "funding_ceiling") : null,
            mayReadContract ? SnapshotJson.Str(r, "currency") : null,
            mayReadContract ? SnapshotJson.Int(r, "settlement_terms_days") : null,
            mayReadContract ? SnapshotJson.Str(r, "invoicing_cadence") : null,
            mayReadContract ? SnapshotJson.Int(r, "claim_submission_window_days") : null);
    }
}

// ---- writes ------------------------------------------------------------------------------------------

/// <summary>The terms half of a create or an update. Optional as a block: a payer can be registered before
/// the agreement is signed, and forcing a ceiling to exist would make an administrator invent one.</summary>
public sealed record PayerTermsInput(
    string? ExternalRef,
    string? AgreementNo,
    DateOnly? AgreementFrom,
    DateOnly? AgreementTo,
    decimal? FundingCeiling,
    string? Currency,
    int? SettlementTermsDays,
    string? InvoicingCadence,
    int? ClaimSubmissionWindowDays);

public sealed record CreatePayer(
    string PayerCode, string NameEn, string NameAr, string PayerType,
    PayerContacts? Contacts = null, PayerTermsInput? Terms = null, string? Notes = null);

/// <summary>
/// An update carries no <c>payerCode</c>, and that is the contract rather than an omission.
///
/// <para>The code is the key extracts, reconciliation files and the payer's own systems join on. Renaming it
/// silently re-points every one of those at a payer they will no longer find, and the failure surfaces as a
/// reconciliation gap weeks later. Everything a payer can legitimately be corrected on — its names, its type,
/// its terms, its contacts — is here; the code is not correctable, it is replaceable, and replacing it means
/// creating the right payer and moving the policies deliberately.</para>
/// </summary>
public sealed record UpdatePayer(
    string NameEn, string NameAr, string PayerType,
    PayerContacts? Contacts = null, PayerTermsInput? Terms = null, string? Notes = null);

/// <summary>Deactivate or reactivate. The reason is REQUIRED and not defaulted: a status change recorded
/// without one preserves the fact and loses the decision, and the decision is the half somebody needs when
/// they find a payer switched off and no living memory of why.</summary>
public sealed record ChangePayerStatus(string Reason);

/// <summary>Reads and writes <see cref="Payer.Contact"/>. Centralised so the jsonb has exactly one shape —
/// it spent four phases as <c>'{}'</c> because nothing owned it, and an unowned blob acquires a second
/// shape the first time two call sites write it.</summary>
public static class PayerContactCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PayerContacts? Read(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        PayerContacts? parsed;
        try { parsed = JsonSerializer.Deserialize<PayerContacts>(raw, Json); }
        catch (JsonException) { return null; }   // a legacy blob of some other shape reads as "no contacts"
        return Normalize(parsed);
    }

    public static string Write(PayerContacts? contacts) =>
        Normalize(contacts) is { } c ? JsonSerializer.Serialize(c, Json) : "{}";

    /// <summary>An entry with every field blank becomes null. Otherwise "no finance contact" and "a finance
    /// contact with nothing filled in" serialize identically and render as an empty card with a heading.</summary>
    private static PayerContacts? Normalize(PayerContacts? c)
    {
        if (c is null) return null;
        var n = new PayerContacts(Clean(c.Primary), Clean(c.Finance), Clean(c.Escalation));
        return n is { Primary: null, Finance: null, Escalation: null } ? null : n;
    }

    private static PayerContact? Clean(PayerContact? c)
    {
        if (c is null || c.IsEmpty) return null;
        return new PayerContact(Trim(c.Name), Trim(c.Title), Trim(c.Email), Trim(c.Phone));
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
