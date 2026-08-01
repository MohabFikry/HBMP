namespace Mersal.Inventory.Domain;

/// <summary>
/// D5 (ADR-0029, design 42 §8) — <b>the clinic-stock catalogue does not admit medicines.</b>
///
/// <para><b>Why this seam exists at all.</b> D5 answered "are vaccines clinic stock or pharmacy stock?" with
/// <i>pharmacy</i>, and for one build that answer was enforced by nothing: no reference to vaccines,
/// injectables or any medicine identifier existed anywhere in this service, so nothing stopped someone
/// cataloguing "Hepatitis B vaccine" as ordinary clinic stock. The sponsor pack recorded that gap
/// (<c>docs/decisions/phase-25-sponsor-pack.md</c>) and this is what closes it.</para>
///
/// <para><b>What the gap actually risked</b> — worth stating precisely, because it is narrower and more
/// specific than "vaccines could leak". The strict invariant held throughout: inventory cannot issue anything
/// to a NAMED patient, because no patient identifier exists anywhere in it (D2, and
/// <c>NoPhiInInventoryTests</c> keeps it that way). What was missing is the paperwork around giving it — no
/// prescription, no eligibility check, no coverage limit, no dispensing record. The vaccine gets given and
/// every control that is supposed to surround giving it happens nowhere.</para>
///
/// <para><b>Why the answer comes from masterdata rather than a list kept here.</b> "What counts as a
/// medicine" is a clinical question and the medicines master is its home. A word list maintained in a
/// storekeeping service would be a second answer to that question, and the two drift the first time a drug is
/// added to one and not the other. Cross-service, therefore, and by VALUE — inventory holds no reference to
/// masterdata rows, it asks a question and gets a verdict.</para>
/// </summary>
public interface IMedicinesDirectory
{
    /// <summary>Is this catalogue entry a medicine? Never throws — an unreachable directory is a
    /// <see cref="MedicineVerdict.DirectoryUnreachable"/> result, so the caller has to decide what to do
    /// about it rather than having an exception decide for them.</summary>
    Task<MedicineCheck> ClassifyAsync(string sku, string nameEn, string? nameAr, CancellationToken ct = default);
}

public enum MedicineVerdict
{
    /// <summary>Nothing in the medicines master matches. Admissible as clinic stock.</summary>
    NotAMedicine,

    /// <summary>It is in the medicines master. Refused — it belongs to pharmacy-service, against an Rx.</summary>
    IsAMedicine,

    /// <summary>The directory could not be reached, so the question is unanswered.
    ///
    /// <para><b>This is refused too, and deliberately.</b> Fail-closed matches <c>HttpBranchDirectory</c>'s
    /// posture in this same service and is the easy call here: catalogue creation is rare, unhurried
    /// reference-data work, so the cost of failing closed is that a new gauze SKU waits a few minutes. The
    /// cost of failing open is a vaccine admitted to clinic stock during the one window nobody was
    /// watching — which is exactly the state this seam was built to end.</para></summary>
    DirectoryUnreachable,
}

/// <summary>The verdict plus, when matched, the public catalogue fields needed to say WHICH medicine — a
/// refusal that names the matched drug is actionable, one that says "this looks like a medicine" is not.</summary>
public sealed record MedicineCheck(
    MedicineVerdict Verdict,
    string? DrugCode = null,
    string? DrugName = null,
    string? AtcCode = null,
    bool IsVaccine = false)
{
    public static readonly MedicineCheck NotAMedicine = new(MedicineVerdict.NotAMedicine);
    public static readonly MedicineCheck Unreachable = new(MedicineVerdict.DirectoryUnreachable);

    /// <summary>The problem type a matched item is refused with. 422 rather than 400: the request is
    /// well-formed and the caller is permitted — the ITEM is in the wrong system.</summary>
    public const string MedicineProblemType = "urn:hbmp:medicine-not-clinic-stock";

    /// <summary>The problem type for an unanswered question. 503 rather than 422: nothing is wrong with what
    /// was asked, and the honest instruction is "retry", not "change your item".</summary>
    public const string UnavailableProblemType = "urn:hbmp:medicines-directory-unavailable";
}
