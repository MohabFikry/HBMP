namespace Mersal.Amendment;

/// <summary>Which order kinds a reason may be cited on. The picker filters on it, so a drug-specific reason
/// never appears on a lab order — a vocabulary that offers nonsense gets used for nonsense.</summary>
public enum ReasonScope { All, Prescription, Order }

/// <param name="Code">The stable identifier. This — not the free text — is what makes "how often do we
/// cancel, and why" answerable.</param>
public sealed record AmendmentReason(string Code, string NameEn, string NameAr, ReasonScope AppliesTo, int SortOrder);

/// <summary>
/// The coded reason vocabulary (design 46 §7, phase-30 Gate 1). <b>This list is canonical.</b>
///
/// <para>Both services seed it into a table of their own — <c>orders.amendment_reason</c> and
/// <c>pharmacy.amendment_reason</c> — so the foreign key is real and cancelling a prescription never depends
/// on masterdata being reachable. A doctor must be able to withdraw a drug during an outage.</para>
///
/// <para>Three copies of a list is three chances to drift, so the drift is made loud instead of silent:
/// <c>AmendmentReasonSeedTests</c> parses both migrations and fails the build if either stops matching this
/// list, in codes, order or Arabic text.</para>
///
/// <para><b>Free text is additional, never instead.</b> The code answers "how often"; the sentence answers
/// "what happened here". Neither substitutes for the other, which is why <c>Other</c> exists and is not a
/// way to avoid choosing — it is the honest answer when none of the seven fits, and it carries the text.</para>
/// </summary>
public static class AmendmentReasons
{
    public static readonly IReadOnlyList<AmendmentReason> All =
    [
        new("PrescribingError", "Prescribing error",   "خطأ في الوصف",         ReasonScope.All,           10),
        new("DoseCorrection",   "Dose correction",     "تصحيح الجرعة",         ReasonScope.Prescription,  20),
        new("PatientDeclined",  "Patient declined",    "رفض المريض",           ReasonScope.All,           30),
        new("ClinicalChange",   "Clinical change",     "تغير الحالة السريرية",  ReasonScope.All,           40),
        new("Duplicate",        "Duplicate",           "مكرر",                 ReasonScope.All,           50),
        new("DrugUnavailable",  "Drug unavailable",    "الدواء غير متوفر",      ReasonScope.Prescription,  60),
        new("NotEligible",      "Patient not eligible","المريض غير مؤهل",       ReasonScope.All,           70),
        new("Other",            "Other",               "أخرى",                 ReasonScope.All,          900),
    ];

    private static readonly HashSet<string> Codes = [.. All.Select(r => r.Code)];

    /// <summary>True when the code exists and may be cited on this kind of order. Unknown codes are refused
    /// rather than stored: a reason column that accepts anything is a free-text column with extra steps, and
    /// every report built on it is quietly wrong.</summary>
    public static bool IsValid(string? code, ReasonScope scope) =>
        code is not null
        && Codes.Contains(code)
        && All.First(r => r.Code == code).AppliesTo is var applies
        && (applies == ReasonScope.All || applies == scope);

    public static IEnumerable<AmendmentReason> For(ReasonScope scope) =>
        All.Where(r => r.AppliesTo == ReasonScope.All || r.AppliesTo == scope).OrderBy(r => r.SortOrder);
}
