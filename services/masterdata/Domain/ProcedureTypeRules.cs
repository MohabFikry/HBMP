namespace Mersal.MasterData.Domain;

/// <summary>Why a procedure type was refused against a code. <c>None</c> means the pairing is valid.</summary>
public enum ProcedureTypeError
{
    None,
    /// <summary>No such type, or the type is inactive. Fail closed — never fall back to "Other".</summary>
    UnknownType,
    /// <summary>The type does not declare the code's CPT section in <c>allowed_cpt_scopes</c>.</summary>
    SectionNotAllowed,
    /// <summary>Sessions were supplied for a type that is not session-based.</summary>
    SessionsOnNonSessionType,
    /// <summary>A session-based type was ordered with no session count, or with a non-positive one.</summary>
    SessionsRequired,
    /// <summary>More sessions than the type permits.</summary>
    SessionsAboveMax,
}

/// <summary>The subset of a <c>masterdata.procedure_type</c> row the rules need.</summary>
public sealed record ProcedureTypeSpec(
    string Code,
    bool IsSessionBased,
    int? DefaultSessions,
    int? MaxSessions,
    IReadOnlyList<string> AllowedCptScopes,
    bool IsActive);

/// <summary>
/// 29.2 — validates a procedure type against the CPT code it accompanies, and its session count against the
/// type (design 45 §2).
///
/// <para>Pure, so the same rules run in the composer, in the write path and in a unit test. The write path is
/// the one that counts: "the composing screen shows it too, but that verdict is display state" — the same
/// reasoning orders-service applies to its section check.</para>
/// </summary>
public static class ProcedureTypeRules
{
    /// <summary>
    /// Whether <paramref name="type"/> may accompany <paramref name="cptCode"/>, with
    /// <paramref name="requestedSessions"/> sessions.
    /// </summary>
    /// <param name="requestedSessions">Null when the composer offered no sessions field.</param>
    public static ProcedureTypeError Validate(ProcedureTypeSpec? type, string? cptCode, int? requestedSessions)
    {
        // Fail closed on an unknown or retired type. Falling back to "Other" would let a decommissioned type
        // keep being ordered under a name nobody administers, and every report grouping by type would
        // silently absorb it.
        if (type is null || !type.IsActive) return ProcedureTypeError.UnknownType;

        var section = CptSections.SectionOf(cptCode);
        if (!type.AllowedCptScopes.Contains(section, StringComparer.OrdinalIgnoreCase))
            return ProcedureTypeError.SectionNotAllowed;

        if (!type.IsSessionBased)
        {
            // Sessions on a non-session type are refused rather than ignored. Silently dropping them would
            // bill one delivery for what the doctor believed was ten.
            return requestedSessions is > 0 ? ProcedureTypeError.SessionsOnNonSessionType : ProcedureTypeError.None;
        }

        if (requestedSessions is not > 0) return ProcedureTypeError.SessionsRequired;
        if (type.MaxSessions is { } max && requestedSessions > max) return ProcedureTypeError.SessionsAboveMax;

        return ProcedureTypeError.None;
    }

    /// <summary>A bilingual explanation for a refusal, for the 422 body.</summary>
    public static (string En, string Ar) Explain(ProcedureTypeError error, ProcedureTypeSpec? type, string? cptCode) =>
        error switch
        {
            ProcedureTypeError.UnknownType => (
                $"'{type?.Code ?? "(none)"}' is not an active procedure type.",
                $"النوع '{type?.Code ?? "(غير محدد)"}' غير متاح."),
            ProcedureTypeError.SectionNotAllowed => (
                $"A {type!.Code} procedure cannot be ordered on {cptCode}, which is a "
                + $"{CptSections.SectionOf(cptCode)} code. This type accepts: {string.Join(", ", type.AllowedCptScopes)}.",
                $"لا يمكن طلب إجراء من نوع {type!.Code} على الكود {cptCode}."),
            ProcedureTypeError.SessionsOnNonSessionType => (
                $"{type!.Code} is not delivered in sessions, so a session count cannot be set.",
                $"النوع {type!.Code} لا يُقدَّم على جلسات، فلا يمكن تحديد عدد الجلسات."),
            ProcedureTypeError.SessionsRequired => (
                $"{type!.Code} is delivered in sessions — a session count is required.",
                $"النوع {type!.Code} يُقدَّم على جلسات — عدد الجلسات مطلوب."),
            ProcedureTypeError.SessionsAboveMax => (
                $"{type!.Code} allows at most {type.MaxSessions} sessions.",
                $"الحد الأقصى لجلسات {type!.Code} هو {type.MaxSessions}."),
            _ => ("", ""),
        };

    /// <summary>
    /// 29.2 — THE deliverable session count, which is the APPROVED one, never the requested one
    /// (design 45 §2).
    ///
    /// <para>If the doctor asks for ten and the approval team partially approves six, the beneficiary is
    /// entitled to six. This is the easiest thing in the gate to get backwards, and getting it backwards
    /// over-supplies the patient and over-consumes their benefit by exactly the difference — silently, because
    /// ten sessions delivered against a six-session approval looks like a completed course from both ends.</para>
    ///
    /// <para>A null approval means the order has not been decided yet and NOTHING is deliverable — not "the
    /// requested amount pending approval". Absence of a decision is never a clean result.</para>
    /// </summary>
    public static int DeliverableSessions(int requestedSessions, int? approvedSessions) =>
        approvedSessions is { } approved ? Math.Max(0, Math.Min(requestedSessions, approved)) : 0;
}
