namespace Mersal.Orders.Domain;

/// <summary>
/// What a check concluded. FIVE states, and the last two are the reason this type exists.
/// </summary>
/// <remarks>
/// Mirrors the prescribing engine's <c>CheckState</c> deliberately — the two workspaces sit on the same
/// screen and a clinician must not have to learn two vocabularies for "this is fine" and "nobody asked".
/// <c>NotChecked</c> (the check does not apply or has no reference data) and <c>Unavailable</c> (it should
/// have run and could not) are never collapsed into <c>Ok</c>: a check that did not run is not a pass.
/// </remarks>
public enum InvestigationCheckState { Ok, Warning, Blocked, NotChecked, Unavailable }

/// <summary>The questions asked of an investigation line.</summary>
public enum InvestigationCheckKind
{
    /// <summary>Is this code real, and is it in master data?</summary>
    Code,
    /// <summary>Is a radiology code being ordered as a lab test, or the reverse?</summary>
    Section,
    /// <summary>Has this same test already been ordered for this patient and not yet come back?</summary>
    Duplicate,
    /// <summary>Will this order stop for pre-authorization rather than go straight to the provider?</summary>
    PriorAuthorization,
    /// <summary>Is the test indicated by what has been diagnosed?</summary>
    Indication,
}

/// <param name="RequiresAcknowledgement">A warning the clinician may proceed past by recording a reason.</param>
/// <param name="IsBlocking">A factual refusal. No reason overrides it.</param>
public sealed record InvestigationFinding(
    Guid LineId, InvestigationCheckKind Kind, InvestigationCheckState State,
    string MessageEn, string MessageAr,
    bool RequiresAcknowledgement, bool IsBlocking,
    string? SourceName = null, string? Caveat = null);

/// <summary>One composed line, as the workspace sends it for checking.</summary>
public sealed record InvestigationLineInput(Guid LineId, string? Code, string? Description, decimal Quantity);

/// <summary>What the checker needs to know that it cannot work out for itself.</summary>
/// <param name="KnownCodes">Codes confirmed present in master data. Null = master data could not be reached.</param>
/// <param name="OpenCodesForPatient">Codes already on an outstanding order for this beneficiary.</param>
/// <param name="GatedCodes">Codes this tenant routes to the approval team.</param>
/// <param name="DiagnosisCount">How many diagnoses the encounter has recorded.</param>
public sealed record InvestigationSnapshot(
    IReadOnlySet<string>? KnownCodes,
    IReadOnlySet<string> OpenCodesForPatient,
    IReadOnlySet<string> GatedCodes,
    int DiagnosisCount);

/// <summary>
/// Advisory checks on a composed investigation order.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and deliberately small. It answers only what this platform can actually establish from data it
/// holds — is the code real, is it the right section for the order being written, is it already outstanding,
/// will it need authorization. It does NOT pretend to a clinical-appropriateness opinion: there is no
/// procedure-indication reference loaded, so that check reports <see cref="InvestigationCheckState.NotChecked"/>
/// with the reason, rather than a silent pass that a clinician would read as approval.
/// </para>
/// <para>
/// The same division as prescribing holds: <b>benefit facts may block, clinical observations may only
/// warn</b>. A repeat test is a warning a doctor may proceed past with a reason — they may know perfectly
/// well the first one is outstanding and want it repeated. An unknown code is blocking, because the order
/// cannot be fulfilled by anyone.
/// </para>
/// </remarks>
public static class InvestigationChecks
{
    /// <summary>CPT sections carried by the code's numeric range, per the CPT book's own organisation.</summary>
    public static bool IsRadiology(string? code) => InRange(code, '7');
    public static bool IsLaboratory(string? code) => InRange(code, '8');

    private static bool InRange(string? code, char lead) =>
        code is { Length: 5 } c && c[0] == lead && c.All(char.IsAsciiDigit);

    public static IReadOnlyList<InvestigationFinding> Evaluate(
        OrderType orderType, IReadOnlyList<InvestigationLineInput> lines, InvestigationSnapshot snapshot)
    {
        var findings = new List<InvestigationFinding>();

        foreach (var line in lines)
        {
            var code = line.Code?.Trim();

            if (string.IsNullOrWhiteSpace(code))
            {
                findings.Add(new(line.LineId, InvestigationCheckKind.Code, InvestigationCheckState.Blocked,
                    "No test has been chosen for this line.", "لم يتم اختيار فحص لهذا السطر.",
                    RequiresAcknowledgement: false, IsBlocking: true));
                continue;
            }

            // ---- Code: real, and in master data ----------------------------------------------------
            if (snapshot.KnownCodes is null)
            {
                // The catalogue could not be asked. NOT a pass: submission re-checks and will refuse, so
                // saying "fine" here would send the clinician into a 422 they were told to expect nothing of.
                findings.Add(new(line.LineId, InvestigationCheckKind.Code, InvestigationCheckState.Unavailable,
                    "The procedure catalogue could not be reached, so this code has not been confirmed.",
                    "تعذّر الوصول إلى كتالوج الإجراءات، لذلك لم يتم التأكد من هذا الكود.",
                    RequiresAcknowledgement: false, IsBlocking: false, SourceName: "masterdata:cpt"));
            }
            else if (!snapshot.KnownCodes.Contains(code))
            {
                findings.Add(new(line.LineId, InvestigationCheckKind.Code, InvestigationCheckState.Blocked,
                    $"'{code}' is not in the procedure catalogue, so no provider could fulfil it.",
                    $"الكود '{code}' غير موجود في كتالوج الإجراءات، لذلك لا يمكن لأي مقدم خدمة تنفيذه.",
                    RequiresAcknowledgement: false, IsBlocking: true, SourceName: "masterdata:cpt"));
            }
            else
            {
                findings.Add(new(line.LineId, InvestigationCheckKind.Code, InvestigationCheckState.Ok,
                    "In the procedure catalogue.", "موجود في كتالوج الإجراءات.",
                    RequiresAcknowledgement: false, IsBlocking: false, SourceName: "masterdata:cpt"));
            }

            // ---- Section: a scan ordered as a blood test cannot be fulfilled -----------------------
            // Blocking rather than a warning: the order goes to a LAB or an IMAGING queue, and a chest
            // x-ray sitting in a haematology worklist is not a judgement call anyone can override — it is
            // simply in the wrong place, and nobody there can do it.
            var wrongSection = orderType switch
            {
                OrderType.Lab when IsRadiology(code) => "a radiology procedure on a laboratory order",
                // 29.1 — both spellings, until the legacy value is dropped (design 45 §1). Naming only the
                // legacy one would turn this BLOCKING check into a silent pass for every Radiology order.
                OrderType.Imaging or OrderType.Radiology when IsLaboratory(code)
                    => "a laboratory procedure on a radiology order",
                _ => null,
            };
            if (wrongSection is not null)
            {
                findings.Add(new(line.LineId, InvestigationCheckKind.Section, InvestigationCheckState.Blocked,
                    $"'{code}' is {wrongSection}. It would reach a queue that cannot perform it.",
                    $"الكود '{code}' من قسم آخر، وسيصل إلى قائمة عمل لا يمكنها تنفيذه.",
                    RequiresAcknowledgement: false, IsBlocking: true));
            }

            // ---- Duplicate: already outstanding for this patient -----------------------------------
            if (snapshot.OpenCodesForPatient.Contains(code))
            {
                findings.Add(new(line.LineId, InvestigationCheckKind.Duplicate, InvestigationCheckState.Warning,
                    "This test is already on an outstanding order for this patient.",
                    "هذا الفحص مطلوب بالفعل ضمن طلب لم يُستكمل بعد لهذا المريض.",
                    RequiresAcknowledgement: true, IsBlocking: false));
            }

            // ---- Prior authorization: a fact about what happens next, not a warning ----------------
            // Deliberately Ok-with-a-message. Making the clinician type a reason to proceed past "this
            // needs approval" would treat a normal, correct route through the benefit scheme as a deviation.
            if (snapshot.GatedCodes.Contains(code))
            {
                findings.Add(new(line.LineId, InvestigationCheckKind.PriorAuthorization, InvestigationCheckState.Ok,
                    "This test needs pre-authorization. The order will go to the approval team, not straight "
                    + "to the provider.",
                    "يتطلب هذا الفحص موافقة مسبقة. سيُحال الطلب إلى فريق الموافقات بدلاً من مقدم الخدمة مباشرة.",
                    RequiresAcknowledgement: false, IsBlocking: false));
            }

            // ---- Indication: honestly not answerable -----------------------------------------------
            findings.Add(snapshot.DiagnosisCount == 0
                ? new(line.LineId, InvestigationCheckKind.Indication, InvestigationCheckState.NotChecked,
                    "No diagnosis is recorded on this encounter, so nothing can be checked against.",
                    "لا يوجد تشخيص مسجل في هذه الزيارة، لذلك لا يوجد ما يمكن التحقق مقابله.",
                    RequiresAcknowledgement: false, IsBlocking: false)
                : new(line.LineId, InvestigationCheckKind.Indication, InvestigationCheckState.NotChecked,
                    "No procedure-indication reference is loaded, so this test has not been checked against "
                    + "the recorded diagnoses.",
                    "لا يوجد مرجع لدواعي إجراء الفحوصات، لذلك لم يتم التحقق من هذا الفحص مقابل التشخيصات المسجلة.",
                    RequiresAcknowledgement: false, IsBlocking: false,
                    Caveat: "The drug-indication reference does not cover procedures."));
        }

        return findings;
    }

    /// <summary>The worst state on a line — what its chip shows.</summary>
    public static InvestigationCheckState StateOf(IEnumerable<InvestigationFinding> lineFindings)
    {
        var states = lineFindings.Select(f => f.State).ToList();
        if (states.Count == 0) return InvestigationCheckState.NotChecked;
        if (states.Contains(InvestigationCheckState.Blocked)) return InvestigationCheckState.Blocked;
        if (states.Contains(InvestigationCheckState.Warning)) return InvestigationCheckState.Warning;
        if (states.Contains(InvestigationCheckState.Unavailable)) return InvestigationCheckState.Unavailable;
        // NotChecked beats Ok: a line whose only unanswered question is the indication must not present as
        // a clean pass, because "checked and fine" and "not looked at" are what this whole scheme separates.
        if (states.Contains(InvestigationCheckState.NotChecked)) return InvestigationCheckState.NotChecked;
        return InvestigationCheckState.Ok;
    }
}
