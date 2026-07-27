using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mersal.Policy.Domain;

// Phase 19.3c — the policy/member change timeline (design 38 §5c). A PROJECTION, never a second log.

/// <summary>What kind of thing happened. Filters group by this, and the icon set is one per category.</summary>
public enum TimelineCategory
{
    Lifecycle, Coverage, Plan, Enrolment, Note, Document, Utilization,
    Authorization, Claim, Access, BulkOperation, Administrative,
}

/// <summary>One entry. Immutable once written; a correction is a new entry referencing it.</summary>
public sealed class TimelineEntry
{
    public Guid EntryId { get; set; }
    public string TenantId { get; set; } = "";
    public NoteScope Scope { get; set; }
    public Guid ScopeRef { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string EventType { get; set; } = default!;
    public TimelineCategory EventCategory { get; set; }

    /// <summary>Snapshotted so history stays readable after a rename or de-provisioning.</summary>
    public Guid? ActorUserId { get; set; }
    public string? ActorUsername { get; set; }
    public string? ActorDisplay { get; set; }

    public string SummaryEn { get; set; } = default!;
    public string SummaryAr { get; set; } = default!;

    /// <summary>Minimized before/after as JSON. Withheld at READ time by class — see
    /// <see cref="TimelineProjection.ProjectDiff"/>.</summary>
    public string? ChangeDiff { get; set; }
    public NoteVisibility VisibilityClass { get; set; } = NoteVisibility.Administrative;

    public string SourceService { get; set; } = default!;
    public string? CorrelationId { get; set; }
    public Guid SourceEventId { get; set; }
    public Guid? TargetRef { get; set; }
    public string? TargetKind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A source event as the projector sees it — an audit event or a domain event, normalized. Keeping
/// the projector's input a plain record is what lets a REPLAY feed it the same values a live projection did.</summary>
public sealed record TimelineSource(
    Guid EventId,
    string EventType,
    NoteScope Scope,
    Guid ScopeRef,
    DateTimeOffset OccurredAt,
    string SourceService,
    Guid? ActorUserId = null,
    string? ActorUsername = null,
    string? ActorDisplay = null,
    string? CorrelationId = null,
    Guid? TargetRef = null,
    string? TargetKind = null,
    NoteVisibility VisibilityClass = NoteVisibility.Administrative,
    IReadOnlyDictionary<string, (string? Before, string? After)>? Changes = null);

/// <summary>
/// Phase 19.3c — the projection, as a pure function.
///
/// <para><b>Deterministic by construction.</b> <see cref="EntryIdFor"/> derives the primary key from the source
/// event id rather than generating one, so re-projecting the same event twice produces the same row and a
/// rebuild is byte-identical rather than merely equivalent. Without that, "replayable" would mean "produces a
/// similar-looking history with different ids", which no diff can verify.</para>
/// </summary>
public static class TimelineProjection
{
    /// <summary>Namespace for the deterministic id, so a timeline entry id can never collide with an id
    /// derived elsewhere from the same event.</summary>
    private static readonly byte[] Namespace = Encoding.UTF8.GetBytes("mersal.policy.entity_timeline.v1");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The entry id for a source event — a stable hash, not a random guid.
    ///
    /// <para>This is what makes a rebuild verifiable. Replaying the audit stream must produce the SAME rows,
    /// not equivalent ones, or the only way to check a rebuild worked is to eyeball it.</para>
    /// </summary>
    public static Guid EntryIdFor(Guid sourceEventId)
    {
        Span<byte> input = stackalloc byte[Namespace.Length + 16];
        Namespace.CopyTo(input);
        sourceEventId.TryWriteBytes(input[Namespace.Length..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }

    /// <summary>Which category an event type belongs to. Unmapped types land in Administrative rather than
    /// being dropped — an event nobody categorized is still something that happened, and silently discarding
    /// it would leave a hole in the history with no trace that anything was missing.</summary>
    public static TimelineCategory CategoryFor(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return eventType switch
        {
            "PolicyIssued" or "PolicyRenewed" or "PolicyStatusChanged" => TimelineCategory.Lifecycle,
            "MemberEnrolled" or "MemberTerminated" or "MemberReinstated" or "MemberGroupChanged"
                => TimelineCategory.Enrolment,
            "MemberPlanChanged" or "PolicyPlanAttached" or "PlanVersionActivated" or "PlanVersionSuperseded"
                => TimelineCategory.Plan,
            "CoverageGenerated" or "CoverageChanged" or "CoverageLimitChanged" or "LimitReset"
                => TimelineCategory.Coverage,
            "NoteAdded" or "NoteCancelled" => TimelineCategory.Note,
            "DocumentAttached" or "DocumentSuperseded" or "DocumentWithdrawn" or "DocumentVerified"
                => TimelineCategory.Document,
            "UtilizationThresholdCrossed" => TimelineCategory.Utilization,
            "AuthorizationDecided" => TimelineCategory.Authorization,
            "ClaimDecided" or "ClaimSettled" => TimelineCategory.Claim,
            // Access events are part of the story, and often the most important line on it (design 19).
            "BreakGlassAccessed" or "RestrictedDocumentDownloaded" or "SensitiveNoteRead"
                => TimelineCategory.Access,
            "BulkJobCompleted" or "BulkJobRolledBack" => TimelineCategory.BulkOperation,
            _ => TimelineCategory.Administrative,
        };
    }

    /// <summary>Project one source event into an entry. Pure — the same input always produces the same row.</summary>
    public static TimelineEntry Project(TimelineSource source, string tenantId, DateTimeOffset projectedAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (en, ar) = Summarize(source);
        return new TimelineEntry
        {
            EntryId = EntryIdFor(source.EventId),
            TenantId = tenantId,
            Scope = source.Scope,
            ScopeRef = source.ScopeRef,
            OccurredAt = source.OccurredAt,
            EventType = source.EventType,
            EventCategory = CategoryFor(source.EventType),
            ActorUserId = source.ActorUserId,
            ActorUsername = source.ActorUsername,
            ActorDisplay = source.ActorDisplay,
            SummaryEn = en,
            SummaryAr = ar,
            ChangeDiff = Minimize(source.Changes),
            VisibilityClass = source.VisibilityClass,
            SourceService = source.SourceService,
            CorrelationId = source.CorrelationId,
            SourceEventId = source.EventId,
            TargetRef = source.TargetRef,
            TargetKind = source.TargetKind,
            CreatedAt = projectedAt,
        };
    }

    /// <summary>
    /// MINIMIZED: only the fields that actually changed, and nothing that did not.
    ///
    /// <para>A diff carrying every field of a row is a copy of the row, and a timeline of copies is a second
    /// database of PHI with none of the controls the first one has.</para>
    /// </summary>
    public static string? Minimize(IReadOnlyDictionary<string, (string? Before, string? After)>? changes)
    {
        if (changes is null || changes.Count == 0) return null;
        var changed = changes
            .Where(kv => !string.Equals(kv.Value.Before, kv.Value.After, StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)   // stable ordering ⇒ byte-identical on replay
            .ToDictionary(kv => kv.Key, kv => new { before = kv.Value.Before, after = kv.Value.After });
        return changed.Count == 0 ? null : JsonSerializer.Serialize(changed, Json);
    }

    /// <summary>
    /// The READ-time diff projection: an operational role sees THAT a clinical record changed, never WHAT it
    /// says.
    ///
    /// <para>Applied at read rather than stored redacted, because a stored-redacted diff would have to be
    /// re-stored every time a role's entitlement changed — and the copy that was already written would still
    /// hold the values.</para>
    /// </summary>
    /// <returns>The diff JSON when the caller may see it; null when it is withheld.</returns>
    public static string? ProjectDiff(
        TimelineEntry entry, IReadOnlyCollection<string> roles, bool hasSensitiveGrant = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.ChangeDiff is null) return null;
        // Reuses the note body rules, deliberately: a second redaction path is how two answers to the same
        // question come to exist. The author check does not apply here — a timeline entry has no author whose
        // own words are being withheld.
        return NoteVisibilityRules.MayReadBody(
            entry.VisibilityClass, roles, userId: null, authorId: Guid.Empty, hasSensitiveGrant)
            ? entry.ChangeDiff
            : null;
    }

    /// <summary>Bilingual one-line summaries. Kept as a table rather than interpolated prose so the Arabic is
    /// authored, not machine-translated at render time.</summary>
    private static (string En, string Ar) Summarize(TimelineSource s) => s.EventType switch
    {
        "PolicyIssued" => ("Policy issued", "تم إصدار الوثيقة"),
        "PolicyRenewed" => ("Policy renewed", "تم تجديد الوثيقة"),
        "PolicyPlanAttached" => ("A plan was attached to the policy", "تمت إضافة خطة إلى الوثيقة"),
        "MemberEnrolled" => ("Member enrolled", "تم تسجيل العضو"),
        "MemberTerminated" => ("Membership terminated", "تم إنهاء العضوية"),
        "MemberReinstated" => ("Membership reinstated", "تمت إعادة العضوية"),
        "MemberGroupChanged" => ("Member moved to another group", "تم نقل العضو إلى مجموعة أخرى"),
        "MemberPlanChanged" => ("Member moved to another plan", "تم نقل العضو إلى خطة أخرى"),
        "CoverageGenerated" => ("Coverage generated from the plan version", "تم إنشاء التغطية من إصدار الخطة"),
        "CoverageChanged" => ("Coverage changed", "تم تغيير التغطية"),
        "CoverageLimitChanged" => ("A benefit limit changed", "تم تغيير حد المنفعة"),
        "LimitReset" => ("A benefit limit reset", "تمت إعادة ضبط حد المنفعة"),
        "UtilizationThresholdCrossed" => ("A utilization threshold was crossed", "تم تجاوز حد الاستخدام"),
        "NoteAdded" => ("A note was added", "تمت إضافة ملاحظة"),
        "NoteCancelled" => ("A note was cancelled", "تم إلغاء ملاحظة"),
        "DocumentAttached" => ("A document was attached", "تم إرفاق مستند"),
        "DocumentSuperseded" => ("A document was replaced by a new version", "تم استبدال المستند بإصدار جديد"),
        "DocumentWithdrawn" => ("A document was withdrawn", "تم سحب المستند"),
        "DocumentVerified" => ("A document was verified", "تم التحقق من المستند"),
        "AuthorizationDecided" => ("An authorization was decided", "تم البت في تصريح"),
        "ClaimDecided" => ("A claim was decided", "تم البت في مطالبة"),
        "ClaimSettled" => ("A claim was settled", "تمت تسوية مطالبة"),
        "BreakGlassAccessed" => ("Break-glass access to this record", "تم الوصول الطارئ إلى هذا السجل"),
        "RestrictedDocumentDownloaded" => ("A restricted document was downloaded", "تم تنزيل مستند مقيّد"),
        "SensitiveNoteRead" => ("A sensitive note was read", "تمت قراءة ملاحظة حساسة"),
        "BulkJobCompleted" => ("A bulk job was applied to this record", "تم تطبيق عملية جماعية على هذا السجل"),
        "BulkJobRolledBack" => ("A bulk job was rolled back", "تم التراجع عن عملية جماعية"),
        // An unmapped type still gets an entry — see CategoryFor. The English falls back to the raw type so
        // the history says what happened even when nobody has written a sentence for it yet.
        _ => (s.EventType, s.EventType),
    };
}
