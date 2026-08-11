using System.Text.Json;

namespace Mersal.Reporting.Infrastructure;

/// <summary>
/// Turns a raw domain event off the wire into a <see cref="ReportingEvent"/> the projectors understand.
///
/// <para><b>This is where the vocabulary is reconciled.</b> The projectors were written against a set of
/// event names, and several of them are not what any service publishes — <c>EncounterCreated</c> against
/// emr's <c>EncounterStarted</c>, <c>AppointmentBooked</c> against <c>ApptBooked</c>,
/// <c>OrderLineConsumed</c> against orders' plural <c>OrderLinesConsumed</c>. The same disease the
/// notification routing table had (§11.3), one layer down: a vocabulary written on one side and never
/// adopted on the other, and nothing fails — the projector's switch simply falls through to
/// <c>default: return false</c> and the event is recorded as processed-but-unmapped. A read model that is
/// silently empty looks exactly like a read model for a quiet week.</para>
///
/// <para><b>Why translate here rather than rename the publishers.</b> Same reasoning as §11.3's answer for
/// notification, and stronger: these names are on the wire between services with live consumers, and the
/// only reader that disagrees is this one. The read model adapts to the platform, not the platform to the
/// read model.</para>
///
/// <para><b>The field bag is flattened, not mapped field-by-field.</b> Every top-level scalar becomes a
/// string entry under its own name, and then a small per-event step renames or derives the handful the
/// projectors read under a different key. Flattening first means a publisher that adds a useful field does
/// not need a change here — and it also means nothing is silently dropped, which matters because the
/// projectors treat a missing field as "unknown" rather than as an error.</para>
///
/// <para><b>Nested objects and arrays are skipped deliberately.</b> A domain payload's nested structures are
/// line detail (<c>lines: [{orderLineId, quantity}]</c>, <c>approvedScope</c>), and the facts here are
/// per-EVENT rather than per-line. Flattening them would invite a fact keyed on the wrong grain.</para>
/// </summary>
public static class ProjectionMapping
{
    /// <summary>
    /// Published event type → the name the projectors switch on.
    ///
    /// <para>Only the ones that differ. An event whose published name already matches passes through, which
    /// is most of them (every <c>Auth*</c>, every <c>Member*</c>, <c>CoverageLimitChanged</c>).</para>
    /// </summary>
    private static readonly Dictionary<string, string> Renames = new(StringComparer.Ordinal)
    {
        ["EncounterStarted"] = "EncounterCreated",
        ["ApptBooked"] = "AppointmentBooked",
        // Checked in IS attended, and it is the only signal emr emits that the person actually arrived.
        ["ApptCheckedIn"] = "AppointmentAttended",
        ["ApptNoShow"] = "AppointmentNoShow",
        ["OrderLinesConsumed"] = "OrderLineConsumed",
        // The three terminal claim decisions all produce one cost fact. `ClaimSettled` is the projector's
        // name for "this claim's money is final", which is what a terminal decision means — a denied claim
        // included, because a denial with a claimed amount and a zero net is a cost fact worth having.
        ["ClaimApproved.v1"] = "ClaimSettled",
        ["ClaimPartiallyApproved.v1"] = "ClaimSettled",
        ["ClaimDenied.v1"] = "ClaimSettled",
        // The per-line twin of the three above. Not a rename of convenience: `ClaimSettled` is one fact for
        // one claim's money and `ClaimLineSettled` is one fact per service line, and the two feed different
        // tables — `fact_cost` and `financial_fact`. Collapsing them would double-count the same money under
        // two grains.
        ["ClaimLineSettled.v1"] = "ClaimLineSettled",
        // Two different creations, one dimension-label fact. policy-service does not publish a
        // "DimensionLabelled" event and should not have to invent one: it publishes that a payer was created
        // and that a plan was attached, and those are the moments a name comes into existence.
        ["PayerCreated"] = "DimensionLabelled",
        ["PolicyPlanAttached"] = "DimensionLabelled",
        // A clinic's name. `dim_label`'s CHECK constraint has reserved `'branch'` since 19.6b and nothing
        // ever wrote one, so the workload and no-show reports rendered a location GUID where a supervisor
        // expected a clinic. Both the creation and the rename map here: a label that only ever learns the
        // original name is a label that goes quietly wrong the first time a clinic is renamed.
        ["BranchCreated"] = "DimensionLabelled",
        ["BranchUpdated"] = "DimensionLabelled",
    };

    /// <summary>The projector name for a published event type, or the type itself when they already agree.</summary>
    public static string ProjectorEventType(string publishedEventType) =>
        Renames.TryGetValue(publishedEventType, out var mapped) ? mapped : publishedEventType;

    /// <summary>
    /// Build the event, or null when the payload cannot be attributed to a tenant.
    /// </summary>
    /// <remarks>
    /// A missing tenant is refused rather than defaulted. Every fact table is tenant-scoped and under RLS, so
    /// a row written under a guessed tenant is one organisation's numbers appearing in another's dashboard —
    /// the same rule <c>DomainEventConsumer</c> applies, for the same reason.
    /// </remarks>
    public static ReportingEvent? TryMap(
        Guid eventId, string publishedEventType, string payload, DateTimeOffset occurredAt)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }

        if (root.ValueKind != JsonValueKind.Object) return null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in root.EnumerateObject())
        {
            var text = Scalar(p.Value);
            if (text is not null) fields[p.Name] = text;
        }

        if (!fields.TryGetValue("tenantId", out var tenant) || string.IsNullOrWhiteSpace(tenant)) return null;

        Derive(publishedEventType, fields);

        return new ReportingEvent(eventId, ProjectorEventType(publishedEventType), tenant, fields, occurredAt);
    }

    /// <summary>
    /// The handful of fields the projectors read under a name the publisher does not use.
    /// </summary>
    /// <remarks>
    /// Additive only — the original key is left in place. A rename that removed the source would make this
    /// table the single point of truth for a field the publisher owns, and the next person adding a fact
    /// would find the field gone with no sign of where it went.
    /// </remarks>
    private static void Derive(string publishedEventType, Dictionary<string, string> fields)
    {
        switch (publishedEventType)
        {
            // `EncounterFact.ClinicId` — emr calls it the location, because that is what a slot is booked
            // against. Same thing.
            case "ApptBooked":
            case "ApptCheckedIn":
            case "ApptNoShow":
                Alias(fields, from: "locationId", to: "clinicId");
                break;

            case "OrderLinesConsumed":
                // Lab vs Radiology is decided by the ORDER TYPE.
                // Through the same additive rule as the aliases: a publisher that starts sending `modality`
                // itself is authoritative about its own event, and this must not clobber it.
                //
                // 29.1 — THIS IS THE IN-FLIGHT-OUTBOX HALF of the design-45 §1 rename, and it is why no
                // reporting change was needed at the switch. The outbox is durable by design, so
                // OrderLinesConsumed events enqueued while orders still said "Imaging" are relayed AFTER the
                // deploy that made it say "Radiology". Both spellings land on the same modality: the legacy
                // value is translated, the canonical one passes through. Covered by
                // ProjectionFeedTests.Order_type_maps_to_modality_under_both_spellings — delete that test and
                // this becomes a silent dimension split, with a month's radiology volume in two buckets.
                if (!fields.ContainsKey("modality") && fields.TryGetValue("orderType", out var orderType))
                    fields["modality"] = orderType.Equals("Imaging", StringComparison.OrdinalIgnoreCase)
                        ? "Radiology" : orderType;
                // One fact per event, so the code is the benefit category rather than a per-line service
                // code: the event carries many lines and the fact has room for one dimension value.
                Alias(fields, from: "benefitCategory", to: "code");
                break;

            case "RxDispensed":
                // The projector's field is named for the ATC class. Pharmacy sends the drug id, because the
                // ATC lives in masterdata-service and resolving it on the dispensing path would put a
                // cross-service call inside the transaction that moves a benefit accumulator. An id is a
                // real code for "which drug"; classing it by ATC is a reporting-side enrichment.
                Alias(fields, from: "drugId", to: "atc");
                break;

            case "CoverageLimitChanged":
                // policy-service names the category on its own events `benefitCategory`; the member
                // utilization fact reads `benefitCategoryCode`.
                Alias(fields, from: "benefitCategory", to: "benefitCategoryCode");
                break;

            // ── The dimension labels ────────────────────────────────────────────────────────────────────
            // `UpsertLabel` is keyed on (dimensionId, kind), so each creation event names which dimension it
            // is labelling. The KIND strings are the ones `AnalyticsQueries.LabelsAsync` queries by; getting
            // one wrong writes a label nothing reads, which looks identical to writing no label at all.
            case "PayerCreated":
                Alias(fields, from: "payerId", to: "dimensionId");
                Alias(fields, from: "payerCode", to: "code");
                Alias(fields, from: "nameEn", to: "labelEn");
                Alias(fields, from: "nameAr", to: "labelAr");
                fields["kind"] = "payer";
                break;

            case "BranchCreated":
            case "BranchUpdated":
                Alias(fields, from: "branchId", to: "dimensionId");
                Alias(fields, from: "branchCode", to: "code");
                Alias(fields, from: "nameEn", to: "labelEn");
                Alias(fields, from: "nameAr", to: "labelAr");
                fields["kind"] = "branch";
                break;

            case "PolicyPlanAttached":
                Alias(fields, from: "policyPlanId", to: "dimensionId");
                // One authored label, used for both languages. A plan label is entered once by an
                // administrator and is not translated anywhere in the product — inventing an Arabic variant
                // here would be a machine translation presented as an authored name.
                Alias(fields, from: "planLabel", to: "labelEn");
                Alias(fields, from: "planLabel", to: "labelAr");
                fields["kind"] = "policy_plan";
                break;
        }
    }

    private static void Alias(Dictionary<string, string> fields, string from, string to)
    {
        if (!fields.ContainsKey(to) && fields.TryGetValue(from, out var v)) fields[to] = v;
    }

    /// <summary>A JSON scalar as the string the field bag holds, or null for objects, arrays and nulls.</summary>
    private static string? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}
