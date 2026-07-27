using System.Globalization;

namespace Mersal.Policy.Domain;

// Phase 19.5b — data extract (design 38 §4.4). The other half of the same engine: bulk upload gets data in,
// extracts get it out, and both run on the 19.5 filter vocabulary in QueryModel.cs rather than on a second
// one of their own.
//
// ============================================================================================================
// AN EXTRACT IS A DISCLOSURE, NOT A REPORT
// ============================================================================================================
// A screen shows 25 rows to one person for a minute. A file leaves the building, gets emailed, sits in a
// Downloads folder and outlives every access decision that produced it. So: the column set is allow-listed per
// role and the withheld columns are NAMED (a silently narrower file is one somebody analyses believing it is
// complete), the run records the filter that was ACTUALLY executed rather than the definition's current
// filter, and the download is a short-TTL signed URL rather than a durable link.

public enum ExtractEntity { Members, Policies, Plans, Coverage, Utilization, NetworkTiers }

public enum ExtractFormat { Csv, Xlsx, Json }

public enum ExtractRunStatus { Queued, Running, Completed, Failed }

/// <summary>
/// What kind of fact a column carries — the axis the role allow-list runs on.
///
/// <para><see cref="Clinical"/> exists as a NAMED, always-denied class rather than as an absence. A column that
/// is simply missing from the catalogue is refused with "unknown column", which reads like a typo; a column
/// refused as clinical tells the requester the rule, and tells the next reviewer that the rule was applied
/// rather than that nobody thought of it.</para>
/// </summary>
public enum ExtractColumnClass { Open, Amounts, Contract, Case, Identity, Clinical }

public sealed record ExtractColumn(string Name, ExtractColumnClass Class);

/// <summary>A column that was asked for and not granted, with the reason — bilingual, because the person
/// reading it is the same person who reads the bulk error file.</summary>
public sealed record WithheldColumn(string Name, string ReasonCode, string ReasonEn, string ReasonAr);

public static class ExtractColumns
{
    // The catalogue. Written out per entity rather than reflected over a DTO for the same reason the CSV
    // export columns in 19.5 are: adding a property to a view must never silently add a column to a file that
    // leaves the organisation.

    public static readonly IReadOnlyList<ExtractColumn> Members =
    [
        new("member_no", ExtractColumnClass.Open),
        new("beneficiary_id", ExtractColumnClass.Open),
        new("given_name", ExtractColumnClass.Identity),
        new("family_name", ExtractColumnClass.Identity),
        new("relationship", ExtractColumnClass.Open),
        new("status", ExtractColumnClass.Open),
        new("effective_from", ExtractColumnClass.Open),
        new("effective_to", ExtractColumnClass.Open),
        new("waiting_period_state", ExtractColumnClass.Open),
        new("branch_id", ExtractColumnClass.Open),
        new("policy_no", ExtractColumnClass.Contract),
        new("payer_id", ExtractColumnClass.Contract),
        new("plan_label", ExtractColumnClass.Open),
        new("plan_version_id", ExtractColumnClass.Open),
        new("group_code", ExtractColumnClass.Open),
        new("total_limit", ExtractColumnClass.Amounts),
        new("total_consumed", ExtractColumnClass.Amounts),
        new("total_remaining", ExtractColumnClass.Amounts),
        new("percent_used", ExtractColumnClass.Amounts),
        new("utilization_band", ExtractColumnClass.Amounts),
        new("termination_reason", ExtractColumnClass.Case),
        // Named so the refusal is a rule and not an oversight. Nothing in policy-service can populate these;
        // they exist here to be denied out loud.
        new("diagnosis", ExtractColumnClass.Clinical),
        new("icd_code", ExtractColumnClass.Clinical),
    ];

    public static readonly IReadOnlyList<ExtractColumn> Policies =
    [
        new("policy_no", ExtractColumnClass.Open),
        new("status", ExtractColumnClass.Open),
        new("effective_from", ExtractColumnClass.Open),
        new("effective_to", ExtractColumnClass.Open),
        new("payer_id", ExtractColumnClass.Contract),
        new("max_members", ExtractColumnClass.Contract),
        new("member_count", ExtractColumnClass.Open),
        new("member_count_band", ExtractColumnClass.Open),
        new("plan_count", ExtractColumnClass.Open),
        new("total_limit", ExtractColumnClass.Amounts),
        new("total_consumed", ExtractColumnClass.Amounts),
        new("percent_used", ExtractColumnClass.Amounts),
        new("utilization_band", ExtractColumnClass.Amounts),
    ];

    public static readonly IReadOnlyList<ExtractColumn> Plans =
    [
        new("plan_label", ExtractColumnClass.Open),
        new("policy_no", ExtractColumnClass.Contract),
        new("plan_version_id", ExtractColumnClass.Open),
        new("version_no", ExtractColumnClass.Open),
        new("version_status", ExtractColumnClass.Open),
        new("effective_from", ExtractColumnClass.Open),
        new("effective_to", ExtractColumnClass.Open),
        new("is_default", ExtractColumnClass.Open),
        new("member_count", ExtractColumnClass.Open),
    ];

    public static readonly IReadOnlyList<ExtractColumn> Coverage =
    [
        new("member_no", ExtractColumnClass.Open),
        new("benefit_category", ExtractColumnClass.Open),
        new("is_covered", ExtractColumnClass.Open),
        new("limit_type", ExtractColumnClass.Open),
        new("reset_period", ExtractColumnClass.Open),
        new("effective_from", ExtractColumnClass.Open),
        new("effective_to", ExtractColumnClass.Open),
        new("limit_value", ExtractColumnClass.Amounts),
        new("consumed_value", ExtractColumnClass.Amounts),
        new("remaining", ExtractColumnClass.Amounts),
    ];

    public static readonly IReadOnlyList<ExtractColumn> Utilization =
    [
        new("member_no", ExtractColumnClass.Open),
        new("benefit_category", ExtractColumnClass.Open),
        new("service_date", ExtractColumnClass.Open),
        new("quantity", ExtractColumnClass.Amounts),
        new("limit_value", ExtractColumnClass.Amounts),
        new("consumed_value", ExtractColumnClass.Amounts),
        new("percent_used", ExtractColumnClass.Amounts),
        new("utilization_band", ExtractColumnClass.Amounts),
        new("provider_id", ExtractColumnClass.Contract),
        new("network_tier", ExtractColumnClass.Contract),
    ];

    public static readonly IReadOnlyList<ExtractColumn> NetworkTiers =
    [
        new("tier_code", ExtractColumnClass.Open),
        new("plan_label", ExtractColumnClass.Open),
        new("benefit_category", ExtractColumnClass.Open),
        new("is_covered", ExtractColumnClass.Open),
        new("copay_fixed", ExtractColumnClass.Amounts),
        new("copay_percent", ExtractColumnClass.Amounts),
        new("coinsurance_percent", ExtractColumnClass.Amounts),
        new("requires_preauth", ExtractColumnClass.Open),
    ];

    public static IReadOnlyList<ExtractColumn> For(ExtractEntity entity) => entity switch
    {
        ExtractEntity.Members => Members,
        ExtractEntity.Policies => Policies,
        ExtractEntity.Plans => Plans,
        ExtractEntity.Coverage => Coverage,
        ExtractEntity.Utilization => Utilization,
        ExtractEntity.NetworkTiers => NetworkTiers,
        _ => throw new ArgumentOutOfRangeException(nameof(entity)),
    };

    /// <summary>The columns a caller gets when they name none. Deliberately the OPEN set only: a default that
    /// included amounts would make the careless request the widest one.</summary>
    public static IReadOnlyList<string> DefaultFor(ExtractEntity entity) =>
        [.. For(entity).Where(c => c.Class == ExtractColumnClass.Open).Select(c => c.Name)];
}

/// <summary>What the caller may see, expressed as the three 19.5 projection capabilities plus identity.</summary>
public sealed record ExtractCapabilities(bool Amounts, bool Contract, bool Case, bool Identity)
{
    public bool Allows(ExtractColumnClass cls) => cls switch
    {
        ExtractColumnClass.Open => true,
        ExtractColumnClass.Amounts => Amounts,
        ExtractColumnClass.Contract => Contract,
        ExtractColumnClass.Case => Case,
        ExtractColumnClass.Identity => Identity,
        _ => false,   // Clinical, and anything added later, deny by default
    };
}

public sealed record ColumnResolution(IReadOnlyList<string> Granted, IReadOnlyList<WithheldColumn> Withheld);

public static class ExtractColumnAllowList
{
    /// <summary>
    /// Intersect what was asked for with what the caller may see.
    ///
    /// <para>Three outcomes, all named: granted, withheld-by-class, and unknown. Silently dropping a column is
    /// specifically rejected by the build prompt, and the reason is arithmetic — a spend report missing
    /// <c>total_consumed</c> without saying so is not a narrower report, it is a wrong one.</para>
    /// </summary>
    public static ColumnResolution Resolve(
        ExtractEntity entity, IReadOnlyList<string>? requested, ExtractCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var catalogue = ExtractColumns.For(entity);
        var asked = requested is null || requested.Count == 0
            ? ExtractColumns.DefaultFor(entity)
            : requested;

        var granted = new List<string>();
        var withheld = new List<WithheldColumn>();

        foreach (var name in asked)
        {
            var column = catalogue.FirstOrDefault(c =>
                string.Equals(c.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

            if (column is null)
            {
                withheld.Add(new WithheldColumn(name ?? "", "UNKNOWN_COLUMN",
                    $"'{name}' is not a column of the {entity} extract.",
                    $"'{name}' ليس عمودًا في تقرير {entity}."));
                continue;
            }

            if (column.Class == ExtractColumnClass.Clinical)
            {
                withheld.Add(new WithheldColumn(column.Name, "CLINICAL_NEVER_EXTRACTED",
                    "Clinical content is never present in a policy or member extract, for any role.",
                    "لا تتضمن تقارير الوثائق أو الأعضاء أي بيانات إكلينيكية، لأي دور وظيفي."));
                continue;
            }

            if (!capabilities.Allows(column.Class))
            {
                withheld.Add(new WithheldColumn(column.Name, "ROLE_NOT_PERMITTED",
                    $"Your role may not read {Describe(column.Class)} on this surface.",
                    $"لا يسمح دورك بالاطلاع على {DescribeAr(column.Class)} في هذا التقرير."));
                continue;
            }

            if (!granted.Contains(column.Name, StringComparer.Ordinal)) granted.Add(column.Name);
        }

        return new ColumnResolution(granted, withheld);
    }

    private static string Describe(ExtractColumnClass cls) => cls switch
    {
        ExtractColumnClass.Amounts => "benefit amounts",
        ExtractColumnClass.Contract => "commercial terms",
        ExtractColumnClass.Case => "case-handling detail",
        ExtractColumnClass.Identity => "beneficiary names",
        _ => "this class of field",
    };

    private static string DescribeAr(ExtractColumnClass cls) => cls switch
    {
        ExtractColumnClass.Amounts => "المبالغ والحدود",
        ExtractColumnClass.Contract => "الشروط التعاقدية",
        ExtractColumnClass.Case => "تفاصيل معالجة الحالة",
        ExtractColumnClass.Identity => "أسماء المستفيدين",
        _ => "هذه الفئة من الحقول",
    };
}

/// <summary>
/// A restricted schedule grammar: <c>@daily</c>, <c>@weekly</c>, <c>@monthly</c>, or <c>m h * * *</c>.
///
/// <para>Deliberately not full cron. An expression this service cannot evaluate would be accepted, stored, and
/// then never fire — and a scheduled extract that silently never runs is discovered by whoever was waiting for
/// the file, months later. Rejecting it at definition time is the only honest option.</para>
/// </summary>
public static class ExtractSchedule
{
    public static bool TryParse(string? cron, out (int Hour, int Minute, string Cadence) schedule)
    {
        schedule = default;
        if (string.IsNullOrWhiteSpace(cron)) return false;

        var raw = cron.Trim().ToLowerInvariant();
        switch (raw)
        {
            case "@daily": schedule = (2, 0, "daily"); return true;
            case "@weekly": schedule = (2, 0, "weekly"); return true;
            case "@monthly": schedule = (2, 0, "monthly"); return true;
        }

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;
        if (parts[2] != "*" || parts[3] != "*" || parts[4] != "*") return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute)
            || minute is < 0 or > 59) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)
            || hour is < 0 or > 23) return false;

        schedule = (hour, minute, "daily");
        return true;
    }
}

/// <summary>A saved extract. <see cref="ServiceScopePayerIds"/> is what a SCHEDULED run executes under —
/// never the creator's ambient rights, which change (or are revoked) long after the schedule was set.</summary>
public sealed class ExtractDefinition
{
    public Guid DefinitionId { get; set; }
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public ExtractEntity Entity { get; set; }
    public string Filter { get; set; } = "{}";
    public string Columns { get; set; } = "[]";
    public ExtractFormat Format { get; set; } = ExtractFormat.Csv;
    public Guid? OwnerUserId { get; set; }
    public bool IsShared { get; set; }
    public string? ScheduleCron { get; set; }

    /// <summary>Comma-separated payer ids a scheduled run is restricted to; empty means the schedule may not
    /// run. A schedule with no explicit scope is not "unrestricted" — it is unconfigured, and the difference
    /// is a nightly file containing every payer's membership.</summary>
    public string? ServiceScopePayerIds { get; set; }

    public bool IsDeleted { get; set; }
    public int RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// One execution. <see cref="FilterSnapshot"/> is WHAT WAS ACTUALLY RUN, not what the definition says today —
/// a definition is editable, and a run that points at a mutable filter cannot answer "what was in the file we
/// sent the donor in March".
/// </summary>
public sealed class ExtractRun
{
    public Guid RunId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid? DefinitionId { get; set; }
    public ExtractEntity Entity { get; set; }
    public Guid? RequestedBy { get; set; }
    public string? RequestedByUsername { get; set; }
    public bool IsScheduled { get; set; }
    public string FilterSnapshot { get; set; } = "{}";
    public string ColumnSnapshot { get; set; } = "[]";
    public string? WithheldSnapshot { get; set; }
    public ExtractFormat Format { get; set; } = ExtractFormat.Csv;
    public DateOnly? AsOf { get; set; }
    public int RowCount { get; set; }
    public Guid? FileDocumentId { get; set; }
    public ExtractRunStatus Status { get; set; } = ExtractRunStatus.Queued;
    public string? FailureDetail { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
