using Mersal.Patient.Domain;

namespace Mersal.Patient.Api;

/// <summary>
/// One standing note as a caller receives it.
///
/// <para><see cref="Value"/> is null exactly when <see cref="Withheld"/> is true. The slot, its label and the
/// fact that it is FILLED are still disclosed, because "no diagnosis is on file" and "a diagnosis is on file
/// that you may not read" are different facts and an operator who cannot tell them apart will ask the
/// beneficiary to repeat information the system already holds.</para>
/// </summary>
public sealed record StandingNoteDto(
    short Slot, string LabelEn, string LabelAr, string Visibility, string? Value, bool Withheld);

/// <summary>
/// Minimum-necessary projection for the six standing note slots.
///
/// <para>Slots 1 and 3 hold clinical facts — a known diagnosis and an insulin flag — captured on a form owned
/// by an administrative role. `18-security-model.md` makes minimum-necessary a matter of code rather than of
/// intent, and beneficiary management sits outside the clinical allow-list for every other carrier of the
/// same material (a scanned lab result, a past medical history). Being the role that TYPED a diagnosis is not
/// a reason to be the role that may read it back: capture is not disclosure.</para>
///
/// <para>Withheld slots are returned as a NAMED locked state rather than dropped. A silently shortened list
/// reads as "nothing was recorded", which is the one wrong answer here.</para>
/// </summary>
public static class NoteProjection
{
    /// <summary>The roles that receive clinical note content. Deliberately the same set the clinical document
    /// class is released to, so a diagnosis typed into slot 1 and a diagnosis scanned into a report do not
    /// answer to two different rules.</summary>
    public static readonly string[] ClinicalReaders =
        ["doctor", "nurse", "medical_approval", "medical_director", "case_manager", "super_admin"];

    public static bool MayReadClinical(IEnumerable<string>? roles) =>
        roles is not null && roles.Any(r => ClinicalReaders.Contains(r, StringComparer.Ordinal));

    public static IReadOnlyList<StandingNoteDto> Project(
        IReadOnlyCollection<RegistrationNote> notes, bool mayReadClinical)
    {
        ArgumentNullException.ThrowIfNull(notes);
        return
        [
            .. notes.OrderBy(n => n.Slot).Select(n =>
            {
                var slot = RegistrationNoteSlots.For(n.Slot);
                var withheld = n.Visibility == NoteVisibility.Clinical && !mayReadClinical;
                return new StandingNoteDto(
                    n.Slot,
                    slot?.LabelEn ?? $"Note {n.Slot}",
                    slot?.LabelAr ?? $"ملاحظة {n.Slot}",
                    n.Visibility.ToString(),
                    withheld ? null : n.Value,
                    withheld);
            }),
        ];
    }
}
