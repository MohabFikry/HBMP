namespace Mersal.Emr.Domain;

/// <summary>
/// One step in a care episode (ADR-0031, migration 0019).
///
/// <para>An appointment is not an event — it is the start of an episode, and almost everything the platform
/// then does for that patient descends from it. This row is one thing that happened in that episode.</para>
///
/// <para><b>A step carries no clinical content.</b> A label, a time, an actor and a business-key reference —
/// <c>"OrderPlaced" · 09:22 · Dr Karim · ORD-2026-000014</c>. The timeline is read by reception and the call
/// centre as well as by clinicians, so a step that named the test or the medicine would put clinical detail
/// in front of a desk that is structurally forbidden it. What a reference resolves to stays behind the
/// owning service's own gate.</para>
/// </summary>
public sealed class CareStep
{
    public Guid StepId { get; set; }
    public string TenantId { get; set; } = "";
    /// <summary>The visit this belongs to. Null only for a step that precedes one (booking, check-in).</summary>
    public Guid? EncounterId { get; set; }
    /// <summary>The episode's parent. Null for a walk-in, whose episode is no less whole for never having
    /// been booked.</summary>
    public Guid? AppointmentId { get; set; }
    public Guid BeneficiaryId { get; set; }
    /// <summary>One of <see cref="CareSteps"/>.</summary>
    public string Step { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    /// <summary>Subject id of whoever did it.</summary>
    public string? Actor { get; set; }
    /// <summary>Which service said so — lets a reader tell a step emr wrote from one that arrived by event,
    /// which is what matters when a step is missing.</summary>
    public string Source { get; set; } = CareStepSources.Emr;
    /// <summary>The business key of the thing this step is about (ENC-*, ORD-*, RX-*, AUTH-*).</summary>
    public string? Reference { get; set; }
    /// <summary>The event that produced it, for at-least-once dedupe. Null for steps emr writes directly.</summary>
    public Guid? EventId { get; set; }
}

/// <summary>The step catalogue (ADR-0031). String constants rather than an enum because the set grows as
/// services join the episode, and every addition would otherwise be a migration in emr — which is the
/// coupling the design exists to avoid.</summary>
public static class CareSteps
{
    // ---- emr's own, in the order a patient experiences them ----
    public const string Booked = "Booked";
    public const string Rescheduled = "Rescheduled";
    public const string CheckedIn = "CheckedIn";
    public const string VisitStarted = "VisitStarted";
    public const string VitalsRecorded = "VitalsRecorded";
    public const string DiagnosisCoded = "DiagnosisCoded";
    public const string NoteSigned = "NoteSigned";
    public const string VisitEnded = "VisitEnded";
    public const string NoShow = "NoShow";
    public const string Cancelled = "Cancelled";

    // ---- from sibling services, once they carry the encounter id (ADR-0031 §"Status") ----
    public const string OrderPlaced = "OrderPlaced";
    public const string OrderSentForApproval = "OrderSentForApproval";
    public const string OrderCancelled = "OrderCancelled";
    public const string SampleConsumed = "SampleConsumed";
    public const string ResultReported = "ResultReported";
    public const string AuthorizationDecided = "AuthorizationDecided";
    public const string PrescriptionWritten = "PrescriptionWritten";
    public const string MedicineDispensed = "MedicineDispensed";
}

public static class CareStepSources
{
    public const string Emr = "emr";
    public const string Orders = "orders";
    public const string Pharmacy = "pharmacy";
    public const string Approvals = "approvals";
}
