namespace Mersal.MasterData.Domain;

/// <summary>What ordering a CPT code actually CREATES. The doctor picks a service; the system decides the
/// vehicle (design 45 §2).</summary>
public enum OrderableVehicle
{
    /// <summary>A <c>Procedure</c> investigation order — Surgery and Medicine. Needs fulfilment and
    /// consumption.</summary>
    ProcedureOrder,

    /// <summary>A <c>Radiology</c> investigation order, through the encounter's existing Radiology tab.</summary>
    RadiologyOrder,

    /// <summary>A <c>Lab</c> investigation order, through the encounter's existing Labs tab.</summary>
    LabOrder,

    /// <summary>A <b>Referral</b>, carrying the CPT code as the requested service — Evaluation and Management.
    /// A referral needs its loop CLOSED with a report back, which a procedure does not; that difference is
    /// exactly why E/M is not simply a procedure with a different label (design 45 §2, invariant 3).</summary>
    Referral,

    /// <summary>Not orderable from an encounter, with a stated reason. Never a silent omission.</summary>
    NotOrderable,
}

/// <summary>Why a code is not orderable — surfaced to the doctor rather than the code simply being absent.</summary>
public sealed record RoutingDecision(OrderableVehicle Vehicle, string Section, string? ReasonEn, string? ReasonAr)
{
    public bool IsOrderable => Vehicle != OrderableVehicle.NotOrderable;
}

/// <summary>
/// 29.2 — the CPT → vehicle routing map (design 45 §2).
///
/// <para><b>The routing input is the code's NUMERIC RANGE, not <c>cpt_code.category</c>.</b> The build prompt
/// says to build this map from the loaded <c>category</c> values and reconcile it against the published
/// ranges. Doing that reveals that the two are not the same axis at all, so there is nothing to reconcile in
/// the direction implied: <c>category</c> holds the CPT <i>taxonomy</i> — the five loaded values are
/// <c>Category I</c> (9,584), <c>Category II</c> (565), <c>Category III</c> (383), <c>PLA</c> (265) and
/// <c>MAAA</c> (13) — which records how a code was adopted into the book, not whether it is a scan, a blood
/// test or an office visit. Routing on it would send every Category I code, from a chest x-ray to a
/// hysterectomy, down one identical path. <see cref="CptSections"/> already carries the section as a pure
/// function of the code, verified against the workbook, and that is the input used here. The reconciliation
/// this DID surface is reported by <c>CptRoutingReconciliation</c> rather than silently resolved.</para>
///
/// <para><b>Where the published ranges and the platform's disagree, the RANGE wins</b> (design 45 §2). Two
/// substantive disagreements exist and both are reported:</para>
/// <list type="bullet">
/// <item>Design 45 §2 lists Medicine as <c>90281–99607</c> and E/M as <c>99202–99499</c> — which OVERLAP.
/// Read literally, every office-visit code is both a Medicine procedure and an E/M referral. The platform's
/// sections carve E/M out of Medicine, so the overlap resolves to E/M, which is plainly the intent: an office
/// visit is the referral case the section describes at length.</item>
/// <item>Design 45 §2's table does not mention <b>Anesthesia</b> (00100–01999) at all, while stating that
/// "every remaining category is orderable". Anesthesia codes are billed alongside a surgery by the
/// anaesthetist, not ordered by a doctor from an outpatient encounter, so they route to
/// <see cref="OrderableVehicle.NotOrderable"/> WITH A REASON. Reported, not silently dropped — a code that
/// simply fails to appear in a picker is indistinguishable from a catalogue gap.</item>
/// </list>
/// </summary>
public static class CptRouting
{
    /// <summary>The vehicle a code's section creates, with a reason when it creates none.</summary>
    public static RoutingDecision For(string? code)
    {
        var section = CptSections.SectionOf(code);
        return section switch
        {
            // Surgery and Medicine are the two OP-Procedure sections (design 45 §2). Medicine is the one that
            // carries physiotherapy, injections, infusions and dialysis — the session-based work Gate 2b's
            // external centres actually deliver.
            CptSections.Surgery or CptSections.Medicine =>
                new(OrderableVehicle.ProcedureOrder, section, null, null),

            CptSections.Imaging =>
                new(OrderableVehicle.RadiologyOrder, section, null, null),

            CptSections.Laboratory or CptSections.Pathology =>
                new(OrderableVehicle.LabOrder, section, null, null),

            // NOT a procedure. A referral needs a loop closed with a report back, and the platform already has
            // the entity, the `referral:write` scope, the state machine and the interop adapter for that.
            CptSections.EvaluationAndManagement =>
                new(OrderableVehicle.Referral, section, null, null),

            CptSections.Anesthesia => new(OrderableVehicle.NotOrderable, section,
                "Anesthesia codes are billed with the procedure they accompany, not ordered from an encounter.",
                "أكواد التخدير تُحتسب مع الإجراء المصاحب لها، ولا تُطلب من الكشف."),

            // Category II (performance measures), Category III (emerging technology), PLA and MAAA. Outside
            // the sectioned body of the book; a performance measure is not a service anyone delivers.
            CptSections.Other => new(OrderableVehicle.NotOrderable, section,
                "Category II/III, PLA and MAAA codes are not orderable services.",
                "أكواد الفئة الثانية/الثالثة وPLA وMAAA ليست خدمات قابلة للطلب."),

            _ => new(OrderableVehicle.NotOrderable, CptSections.Other,
                "Not a recognised CPT code.",
                "ليس كودًا معتمدًا في CPT."),
        };
    }

    /// <summary>The order type a <see cref="OrderableVehicle"/> maps to, or null for a referral / non-order.
    /// Kept here so orders-service and the UI agree without either owning the other's vocabulary.</summary>
    public static string? OrderTypeFor(OrderableVehicle vehicle) => vehicle switch
    {
        OrderableVehicle.ProcedureOrder => "Procedure",
        OrderableVehicle.RadiologyOrder => "Radiology",
        OrderableVehicle.LabOrder => "Lab",
        _ => null,
    };
}
