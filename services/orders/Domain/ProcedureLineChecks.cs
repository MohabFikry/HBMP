namespace Mersal.Orders.Domain;

/// <summary>Why a Procedure line was refused. <c>None</c> means it may be written.</summary>
public enum ProcedureLineError
{
    None,
    /// <summary>A Procedure order line carries no procedure type.</summary>
    TypeMissing,
    /// <summary>The type is unknown, retired, or masterdata could not be reached.</summary>
    TypeUnknown,
    /// <summary>The type does not accept the code's CPT section.</summary>
    TypeSectionMismatch,
    /// <summary>Sessions on a type that is not delivered in sessions.</summary>
    SessionsNotSupported,
    /// <summary>More sessions than the type permits.</summary>
    SessionsAboveMax,
    /// <summary>A procedure type on a Lab or Radiology line.</summary>
    TypeOnNonProcedureOrder,
}

/// <summary>The type facts the check needs, so the rule is pure and the HTTP resolver stays at the edge.</summary>
public sealed record ProcedureTypeFacts(
    string Code, bool IsSessionBased, int? MaxSessions, IReadOnlyList<string> AllowedCptSections, bool IsActive);

/// <summary>
/// 29.2 — validates a Procedure order line's type against its code, on the WRITE path (design 45 §2).
///
/// <para>Pure, and deliberately independent of masterdata's own <c>ProcedureTypeRules</c>: the two services
/// each own their side of the contract, and orders must be able to refuse a line without trusting a verdict
/// computed elsewhere. The CPT section is supplied by the caller rather than derived here, because
/// <c>CptSections</c> lives in masterdata's domain and orders should not grow a second copy of the range
/// table — a second copy is how the two come to disagree about where Medicine ends.</para>
/// </summary>
public static class ProcedureLineChecks
{
    public static ProcedureLineError Validate(
        OrderType orderType, string? procedureTypeCode, string? cptSection,
        decimal requestedQuantity, ProcedureTypeFacts? facts)
    {
        var canonical = OrderTypes.Canonical(orderType);

        if (canonical != OrderType.Procedure)
        {
            // A type on a lab or radiology line is refused rather than ignored. Silently dropping it would
            // make every report grouped by procedure type quietly incomplete, in the direction that looks
            // like "we do less physiotherapy than we do".
            return string.IsNullOrWhiteSpace(procedureTypeCode)
                ? ProcedureLineError.None
                : ProcedureLineError.TypeOnNonProcedureOrder;
        }

        if (string.IsNullOrWhiteSpace(procedureTypeCode)) return ProcedureLineError.TypeMissing;

        // Fail closed on unknown, retired, OR unreachable — the resolver returns null for all three, and that
        // is correct: "masterdata did not answer" is not a reason to write a line whose type nobody validated.
        if (facts is null || !facts.IsActive) return ProcedureLineError.TypeUnknown;

        if (!facts.AllowedCptSections.Contains(cptSection ?? "", StringComparer.OrdinalIgnoreCase))
            return ProcedureLineError.TypeSectionMismatch;

        if (!facts.IsSessionBased)
        {
            // Sessions are the line's QUANTITY, so "more than one" on a non-session type is a session count
            // wearing a quantity's clothes.
            return requestedQuantity > 1 ? ProcedureLineError.SessionsNotSupported : ProcedureLineError.None;
        }

        if (facts.MaxSessions is { } max && requestedQuantity > max) return ProcedureLineError.SessionsAboveMax;

        return ProcedureLineError.None;
    }

    /// <summary>A bilingual explanation for the 422 body. Never a generic error — the doctor has to know
    /// which of the two fields to change.</summary>
    public static (string En, string Ar) Explain(
        ProcedureLineError error, string? typeCode, string? code, string? section, ProcedureTypeFacts? facts) =>
        error switch
        {
            ProcedureLineError.TypeMissing => (
                "A procedure order line needs a procedure type.",
                "طلب الإجراء يحتاج إلى تحديد نوع الإجراء."),
            ProcedureLineError.TypeUnknown => (
                $"'{typeCode}' is not an active procedure type, or master data could not be reached.",
                $"النوع '{typeCode}' غير متاح، أو تعذّر الوصول إلى البيانات المرجعية."),
            ProcedureLineError.TypeSectionMismatch => (
                $"A {typeCode} procedure cannot be ordered on {code}, which is a {section} code. "
                + $"This type accepts: {string.Join(", ", facts?.AllowedCptSections ?? [])}.",
                $"لا يمكن طلب إجراء من نوع {typeCode} على الكود {code}."),
            ProcedureLineError.SessionsNotSupported => (
                $"{typeCode} is not delivered in sessions, so a quantity above 1 cannot be ordered.",
                $"النوع {typeCode} لا يُقدَّم على جلسات، فلا يمكن طلب كمية أكبر من واحد."),
            ProcedureLineError.SessionsAboveMax => (
                $"{typeCode} allows at most {facts?.MaxSessions} sessions.",
                $"الحد الأقصى لجلسات {typeCode} هو {facts?.MaxSessions}."),
            ProcedureLineError.TypeOnNonProcedureOrder => (
                "A procedure type can only be set on a Procedure order.",
                "لا يمكن تحديد نوع الإجراء إلا على طلبات الإجراءات."),
            _ => ("", ""),
        };
}
