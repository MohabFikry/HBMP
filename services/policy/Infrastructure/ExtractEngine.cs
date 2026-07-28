using Mersal.BenefitPricing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Mersal.Audit.Client;
using Mersal.Authz;
using Mersal.Policy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Infrastructure;

/// <summary>
/// Phase 19.5b — run a data extract.
///
/// <para>The filter vocabulary is the 19.5 one (<c>Domain/QueryModel.cs</c>) and the payer restriction is the
/// 19.5 one (<c>libs/authz/PayerScope.cs</c>). That is deliberate to the point of being the design: a member
/// who appears in a query and not in an extract, or vice versa, means one of the two screens is lying, and
/// there is no way to tell which from either screen alone.</para>
/// </summary>
public sealed class ExtractEngine(
    PolicyDbContext db,
    IOperationalDocumentStore documents,
    IAuditClient audit,
    TimeProvider clock)
{
    /// <summary>The most rows one extract may produce. Not a performance number — a blast radius. Beyond this
    /// the caller is asked to narrow the filter, which is a prompt to think about what they actually need.</summary>
    public const int MaxRows = 100_000;

    /// <summary>Above this the file is stored and downloaded rather than streamed inline.</summary>
    public const int StreamThreshold = 5_000;

    public async Task<ExtractResult> RunAsync(
        ExtractRequest request, ExtractCapabilities capabilities, PermittedPayers payers,
        ActorRef actor, string? actorUsername, string? bearerToken, bool isScheduled = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolution = ExtractColumnAllowList.Resolve(request.Entity, request.Columns, capabilities);
        if (resolution.Granted.Count == 0)
            return ExtractResult.Refused(
                "No column of this extract is available to your role, so the file would be empty.",
                resolution.Withheld);

        var now = clock.GetUtcNow();
        var run = new ExtractRun
        {
            RunId = Guid.NewGuid(), DefinitionId = request.DefinitionId, Entity = request.Entity,
            RequestedBy = actor?.UserId, RequestedByUsername = actorUsername, IsScheduled = isScheduled,
            // The filter that was ACTUALLY executed. A definition is editable; a run pointing at a mutable
            // filter cannot answer "what was in the file we sent the donor in March".
            FilterSnapshot = JsonSerializer.Serialize(request.Filter, JsonOptions),
            ColumnSnapshot = JsonSerializer.Serialize(resolution.Granted, JsonOptions),
            WithheldSnapshot = resolution.Withheld.Count == 0
                ? null : JsonSerializer.Serialize(resolution.Withheld, JsonOptions),
            Format = request.Format, AsOf = request.Filter.AsOf,
            Status = ExtractRunStatus.Running, StartedAt = now,
        };
        db.ExtractRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            var rows = await QueryAsync(request.Entity, request.Filter, payers, ct);
            if (rows.Count > MaxRows)
            {
                run.Status = ExtractRunStatus.Failed;
                run.FailureDetail = $"The filter matches more than {MaxRows:N0} rows; narrow it.";
                run.CompletedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
                return ExtractResult.Refused(run.FailureDetail, resolution.Withheld);
            }

            var projected = rows
                .Select(r => (IReadOnlyList<string?>)[.. resolution.Granted.Select(c => Render(r.GetValueOrDefault(c)))])
                .ToList();

            var bytes = Serialize(request.Format, resolution.Granted, projected, request.Entity);
            run.RowCount = rows.Count;
            run.Status = ExtractRunStatus.Completed;
            run.CompletedAt = clock.GetUtcNow();

            Guid? documentId = null;
            var inline = rows.Count <= StreamThreshold;
            if (!inline)
            {
                documentId = await documents.StoreAsync(
                    "Extract", run.RunId, FileName(request, run), ContentType(request.Format), bytes, bearerToken, ct);
                run.FileDocumentId = documentId;
                if (documentId is null)
                {
                    run.Status = ExtractRunStatus.Failed;
                    run.FailureDetail = "The extract ran but could not be stored; nothing was disclosed.";
                    await db.SaveChangesAsync(ct);
                    return ExtractResult.Refused(run.FailureDetail, resolution.Withheld);
                }
            }
            await db.SaveChangesAsync(ct);

            // Every run is audited with the filter snapshot, the row count AND the column set. All three,
            // because "somebody ran the members extract" does not tell a later review what left the building.
            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "extract_run", EntityId = run.RunId.ToString(), Action = AuditAction.Export,
                ActorUserId = actor?.Subject ?? actorUsername,
                DecisionOutcome = $"entity={request.Entity};rows={rows.Count};columns={resolution.Granted.Count}" +
                                  (resolution.Withheld.Count > 0 ? $";withheld={resolution.Withheld.Count}" : ""),
                DecisionReasonCode = (isScheduled ? "scheduled;" : "") + run.FilterSnapshot,
                FieldClasses = ["coverage"],
                Severity = AuditSeverity.Notice,
            }, ct);

            return new ExtractResult(run, resolution.Granted, resolution.Withheld, rows.Count,
                inline ? bytes : null, documentId, FileName(request, run), ContentType(request.Format), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = ExtractRunStatus.Failed;
            run.FailureDetail = ex.Message;
            run.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    // ---- Queries -----------------------------------------------------------------------------------------

    private Task<List<Dictionary<string, object?>>> QueryAsync(
        ExtractEntity entity, ExtractFilter filter, PermittedPayers payers, CancellationToken ct) => entity switch
        {
            ExtractEntity.Members => MembersAsync(filter, payers, ct),
            ExtractEntity.Policies => PoliciesAsync(filter, payers, ct),
            ExtractEntity.Plans => PlansAsync(filter, payers, ct),
            ExtractEntity.Coverage => CoverageAsync(filter, payers, ct),
            ExtractEntity.Utilization => UtilizationAsync(filter, payers, ct),
            ExtractEntity.NetworkTiers => NetworkTiersAsync(filter, payers, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(entity)),
        };

    /// <summary>
    /// The member list, optionally AS OF a date.
    ///
    /// <para>Without an as-of this is the 19.5 member query without paging. With one, the window predicate
    /// selects on the membership's own effective dates — which is what makes a member terminated on 15 March
    /// still appear in a 1-March extract — and the plan is then reconstructed from enrollment_event, because
    /// the CURRENT row shows the plan they are on today, not the one they were on in March.</para>
    /// </summary>
    private async Task<List<Dictionary<string, object?>>> MembersAsync(
        ExtractFilter filter, PermittedPayers payers, CancellationToken ct)
    {
        var scopedPolicies = ScopedPolicies(payers);
        var query = db.Enrollments.AsNoTracking().Where(e => !e.IsDeleted)
            .Where(e => scopedPolicies.Contains(e.PolicyId));

        if (filter.PolicyId is { } policyId) query = query.Where(e => e.PolicyId == policyId);
        if (filter.PolicyPlanId is { } planId) query = query.Where(e => e.PolicyPlanId == planId);
        if (filter.GroupId is { } groupId) query = query.Where(e => e.GroupId == groupId);
        if (filter.BranchId is { } branchId) query = query.Where(e => e.BranchId == branchId);
        if (filter.Relationship is { } relationship) query = query.Where(e => e.Relationship == relationship);
        if (filter.EnrolledFrom is { } from) query = query.Where(e => e.EffectiveFrom >= from);
        if (filter.EnrolledTo is { } to) query = query.Where(e => e.EffectiveFrom <= to);

        if (filter.AsOf is { } asOf)
        {
            // Inclusive both ends — the membership window's own convention (EnrollmentEntities.cs). A member
            // whose cover ended ON the as-of date was covered that day.
            query = query.Where(e => e.EffectiveFrom <= asOf
                                     && (e.EffectiveTo == null || e.EffectiveTo >= asOf)
                                     && e.Status != EnrollmentStatus.Cancelled);
        }
        else if (filter.MemberStatus is { } status)
        {
            query = query.Where(e => e.Status == status);
        }

        var enrollments = await query.OrderBy(e => e.MemberNo).Take(MaxRows + 1).ToListAsync(ct);
        if (enrollments.Count == 0) return [];

        var enrollmentIds = enrollments.Select(e => e.EnrollmentId).ToList();
        var policyIds = enrollments.Select(e => e.PolicyId).Distinct().ToList();

        var policies = await db.Policies.AsNoTracking()
            .Where(p => policyIds.Contains(p.PolicyId))
            .ToDictionaryAsync(p => p.PolicyId, p => p, ct);
        var plans = await db.PolicyPlans.AsNoTracking()
            .Where(pp => policyIds.Contains(pp.PolicyId))
            .ToDictionaryAsync(pp => pp.PolicyPlanId, pp => pp, ct);
        var groups = await db.MemberGroups.AsNoTracking()
            .Where(g => policyIds.Contains(g.PolicyId))
            .ToDictionaryAsync(g => g.GroupId, g => g.GroupCode, ct);

        // The accumulator totals, summed in SQL — the same numbers 19.4 reports, so an extract and a
        // utilization screen cannot disagree about how much somebody has used.
        var totals = await db.Coverages.AsNoTracking()
            .Where(c => enrollmentIds.Contains(c.EnrollmentId!.Value) && !c.IsDeleted)
            .SelectMany(c => c.Limits.Select(l => new { c.EnrollmentId, l.LimitValue, l.ConsumedValue }))
            .GroupBy(x => x.EnrollmentId)
            .Select(g => new
            {
                EnrollmentId = g.Key,
                Limit = g.Sum(x => x.LimitValue),
                Consumed = g.Sum(x => x.ConsumedValue),
                Rows = g.Count(),
            })
            .ToDictionaryAsync(x => x.EnrollmentId!.Value, x => x, ct);

        var eventsByEnrollment = new Dictionary<Guid, List<AsOfEvent>>();
        if (filter.AsOf is not null)
        {
            var events = await db.EnrollmentEvents.AsNoTracking()
                .Where(e => enrollmentIds.Contains(e.EnrollmentId))
                .ToListAsync(ct);
            foreach (var group in events.GroupBy(e => e.EnrollmentId))
                eventsByEnrollment[group.Key] = [.. group.Select(AsOfEvent.From)];
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var rows = new List<Dictionary<string, object?>>(enrollments.Count);
        foreach (var e in enrollments)
        {
            var rowPlanId = e.PolicyPlanId;
            var rowGroupId = e.GroupId;
            var rowStatus = e.Status;
            var approximate = false;

            if (filter.AsOf is { } on)
            {
                var state = AsOfMembership.Reconstruct(
                    e, eventsByEnrollment.GetValueOrDefault(e.EnrollmentId, []), on);
                if (!state.WasMember) continue;
                rowPlanId = state.PolicyPlanId ?? rowPlanId;
                rowGroupId = state.GroupId;
                rowStatus = state.Status;
                approximate = state.PlanApproximate;
            }

            var total = totals.GetValueOrDefault(e.EnrollmentId);
            var limit = total?.Limit ?? 0m;
            var consumed = total?.Consumed ?? 0m;
            var band = UtilizationBands.Of(limit, consumed, (total?.Rows ?? 0) > 0);
            if (filter.UtilizationBand is { } wanted && band != wanted) continue;

            var waitingState = e.WaitingPeriodEndsOn is null ? WaitingPeriodState.None
                : e.InWaitingPeriod(filter.AsOf ?? today) ? WaitingPeriodState.Serving
                : WaitingPeriodState.Served;
            if (filter.WaitingPeriod is { } wp && waitingState != wp) continue;

            var plan = plans.GetValueOrDefault(rowPlanId);
            var policy = policies.GetValueOrDefault(e.PolicyId);
            if (filter.PlanLabel is { } label && !string.Equals(plan?.PlanLabel, label, StringComparison.OrdinalIgnoreCase))
                continue;

            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["member_no"] = e.MemberNo,
                ["beneficiary_id"] = e.BeneficiaryId,
                // Names are not in the policy schema. Left EMPTY rather than filled from a stale local copy:
                // an extract with a name that patient-service corrected last month is worse than one without.
                ["given_name"] = null,
                ["family_name"] = null,
                ["relationship"] = e.Relationship.ToString(),
                ["status"] = rowStatus.ToString(),
                ["effective_from"] = e.EffectiveFrom,
                ["effective_to"] = e.EffectiveTo,
                ["waiting_period_state"] = waitingState.ToString(),
                ["branch_id"] = e.BranchId,
                ["policy_no"] = policy?.PolicyNo,
                ["payer_id"] = policy?.PayerId,
                // The approximation is CARRIED INTO THE VALUE, not dropped. A membership predating 19.5b has no
                // dated event naming its plan, and a reader must be able to tell a reconstructed plan from an
                // assumed one — silently showing today's plan for a March extract is the failure this avoids.
                ["plan_label"] = approximate && plan is not null ? $"{plan.PlanLabel} (current; not reconstructed)" : plan?.PlanLabel,
                ["plan_version_id"] = plan?.PlanVersionId,
                ["group_code"] = rowGroupId is { } g ? groups.GetValueOrDefault(g) : null,
                ["total_limit"] = limit,
                ["total_consumed"] = consumed,
                ["total_remaining"] = Math.Max(limit - consumed, 0m),
                ["percent_used"] = UtilizationBands.PercentUsed(limit, consumed),
                ["utilization_band"] = band.ToString(),
                ["termination_reason"] = e.TerminationReason,
            });
        }

        return rows;
    }

    private async Task<List<Dictionary<string, object?>>> PoliciesAsync(
        ExtractFilter filter, PermittedPayers payers, CancellationToken ct)
    {
        var query = db.Policies.AsNoTracking().Where(p => !p.IsDeleted);
        query = ApplyPayerScope(query, payers);
        if (filter.PayerId is { } payerId) query = query.Where(p => p.PayerId == payerId);
        if (filter.PolicyId is { } policyId) query = query.Where(p => p.PolicyId == policyId);

        var policies = await query.OrderBy(p => p.PolicyNo).Take(MaxRows + 1).ToListAsync(ct);
        var ids = policies.Select(p => p.PolicyId).ToList();

        var memberCounts = await db.Enrollments.AsNoTracking()
            .Where(e => ids.Contains(e.PolicyId) && !e.IsDeleted && e.Status == EnrollmentStatus.Active)
            .GroupBy(e => e.PolicyId).Select(g => new { PolicyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PolicyId, x => x.Count, ct);
        var planCounts = await db.PolicyPlans.AsNoTracking()
            .Where(pp => ids.Contains(pp.PolicyId) && !pp.IsDeleted)
            .GroupBy(pp => pp.PolicyId).Select(g => new { PolicyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PolicyId, x => x.Count, ct);
        var totals = await db.Coverages.AsNoTracking()
            .Where(c => ids.Contains(c.PolicyId) && !c.IsDeleted)
            .SelectMany(c => c.Limits.Select(l => new { c.PolicyId, l.LimitValue, l.ConsumedValue }))
            .GroupBy(x => x.PolicyId)
            .Select(g => new { PolicyId = g.Key, Limit = g.Sum(x => x.LimitValue), Consumed = g.Sum(x => x.ConsumedValue) })
            .ToDictionaryAsync(x => x.PolicyId, x => x, ct);

        var rows = new List<Dictionary<string, object?>>();
        foreach (var p in policies)
        {
            var members = memberCounts.GetValueOrDefault(p.PolicyId);
            var total = totals.GetValueOrDefault(p.PolicyId);
            var limit = total?.Limit ?? 0m;
            var consumed = total?.Consumed ?? 0m;
            var band = UtilizationBands.Of(limit, consumed, total is not null);
            if (filter.UtilizationBand is { } wanted && band != wanted) continue;

            rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["policy_no"] = p.PolicyNo,
                ["status"] = p.Status.ToString(),
                ["effective_from"] = p.EffectiveFrom,
                ["effective_to"] = p.EffectiveTo,
                ["payer_id"] = p.PayerId,
                ["max_members"] = p.MaxMembers,
                ["member_count"] = members,
                ["member_count_band"] = MemberCountBands.Of(members).ToString(),
                ["plan_count"] = planCounts.GetValueOrDefault(p.PolicyId),
                ["total_limit"] = limit,
                ["total_consumed"] = consumed,
                ["percent_used"] = UtilizationBands.PercentUsed(limit, consumed),
                ["utilization_band"] = band.ToString(),
            });
        }
        return rows;
    }

    private async Task<List<Dictionary<string, object?>>> PlansAsync(
        ExtractFilter filter, PermittedPayers payers, CancellationToken ct)
    {
        var scopedPolicies = ScopedPolicies(payers);
        var query = db.PolicyPlans.AsNoTracking().Where(pp => !pp.IsDeleted)
            .Where(pp => scopedPolicies.Contains(pp.PolicyId));
        if (filter.PolicyId is { } policyId) query = query.Where(pp => pp.PolicyId == policyId);
        if (filter.PlanLabel is { } label) query = query.Where(pp => EF.Functions.ILike(pp.PlanLabel, label));

        var plans = await query.OrderBy(pp => pp.PlanLabel).Take(MaxRows + 1).ToListAsync(ct);
        var policyNos = await db.Policies.AsNoTracking()
            .Where(p => plans.Select(pp => pp.PolicyId).Contains(p.PolicyId))
            .ToDictionaryAsync(p => p.PolicyId, p => p.PolicyNo, ct);
        var versions = await db.PlanVersions.AsNoTracking()
            .Where(v => plans.Select(pp => pp.PlanVersionId).Contains(v.PlanVersionId))
            .ToDictionaryAsync(v => v.PlanVersionId, v => v, ct);
        var counts = await db.Enrollments.AsNoTracking()
            .Where(e => !e.IsDeleted && e.Status == EnrollmentStatus.Active)
            .GroupBy(e => e.PolicyPlanId).Select(g => new { PlanId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PlanId, x => x.Count, ct);

        return [.. plans.Select(pp =>
        {
            var version = versions.GetValueOrDefault(pp.PlanVersionId);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["plan_label"] = pp.PlanLabel,
                ["policy_no"] = policyNos.GetValueOrDefault(pp.PolicyId),
                ["plan_version_id"] = pp.PlanVersionId,
                ["version_no"] = version?.VersionNo,
                ["version_status"] = version?.Status.ToString(),
                ["effective_from"] = pp.EffectiveFrom,
                ["effective_to"] = pp.EffectiveTo,
                ["is_default"] = pp.IsDefault,
                ["member_count"] = counts.GetValueOrDefault(pp.PolicyPlanId),
            };
        })];
    }

    private async Task<List<Dictionary<string, object?>>> CoverageAsync(
        ExtractFilter filter, PermittedPayers payers, CancellationToken ct)
    {
        var scopedPolicies = ScopedPolicies(payers);
        var query = db.Coverages.AsNoTracking().Include(c => c.Limits)
            .Where(c => !c.IsDeleted && scopedPolicies.Contains(c.PolicyId));
        if (filter.PolicyId is { } policyId) query = query.Where(c => c.PolicyId == policyId);

        var coverages = await query.Take(MaxRows + 1).ToListAsync(ct);
        var enrollmentIds = coverages.Where(c => c.EnrollmentId is not null).Select(c => c.EnrollmentId!.Value).Distinct().ToList();
        var memberNos = await db.Enrollments.AsNoTracking()
            .Where(e => enrollmentIds.Contains(e.EnrollmentId))
            .ToDictionaryAsync(e => e.EnrollmentId, e => e.MemberNo, ct);
        var categories = await db.BenefitCategories.AsNoTracking().ToDictionaryAsync(c => c.BenefitCategoryId, c => c.Code, ct);

        var rows = new List<Dictionary<string, object?>>();
        foreach (var c in coverages)
        {
            var code = categories.GetValueOrDefault(c.BenefitCategoryId);
            if (filter.BenefitCategory is { } wanted && !string.Equals(code, wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var l in c.Limits)
            {
                rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["member_no"] = c.EnrollmentId is { } id ? memberNos.GetValueOrDefault(id) : null,
                    ["benefit_category"] = code,
                    ["is_covered"] = c.Status == CoverageStatus.Active,
                    ["limit_type"] = l.LimitType.ToString(),
                    ["reset_period"] = l.ResetPeriod.ToString(),
                    ["effective_from"] = c.EffectiveFrom,
                    ["effective_to"] = c.EffectiveTo,
                    ["limit_value"] = l.LimitValue,
                    ["consumed_value"] = l.ConsumedValue,
                    ["remaining"] = Math.Max(l.LimitValue - l.ConsumedValue, 0m),
                });
            }
        }
        return rows;
    }

    private async Task<List<Dictionary<string, object?>>> UtilizationAsync(
        ExtractFilter filter, PermittedPayers payers, CancellationToken ct)
    {
        var scopedPolicies = ScopedPolicies(payers);
        var scopedBeneficiaries = db.Enrollments.AsNoTracking()
            .Where(e => !e.IsDeleted && scopedPolicies.Contains(e.PolicyId))
            .Select(e => e.BeneficiaryId);

        var query = db.BenefitConsumptions.AsNoTracking()
            .Where(x => scopedBeneficiaries.Contains(x.BeneficiaryId));
        if (filter.ServiceFrom is { } from) query = query.Where(x => x.ServiceDate >= from);
        if (filter.ServiceTo is { } to) query = query.Where(x => x.ServiceDate <= to);
        if (filter.BenefitCategory is { } category) query = query.Where(x => x.BenefitCategory == category);

        var consumption = await query.OrderByDescending(x => x.AppliedAt).Take(MaxRows + 1).ToListAsync(ct);
        var beneficiaryIds = consumption.Select(x => x.BeneficiaryId).Distinct().ToList();
        var memberNos = await db.Enrollments.AsNoTracking()
            .Where(e => beneficiaryIds.Contains(e.BeneficiaryId) && !e.IsDeleted)
            .GroupBy(e => e.BeneficiaryId)
            .Select(g => new { BeneficiaryId = g.Key, MemberNo = g.Min(e => e.MemberNo) })
            .ToDictionaryAsync(x => x.BeneficiaryId, x => x.MemberNo, ct);

        return [.. consumption.Select(x => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["member_no"] = memberNos.GetValueOrDefault(x.BeneficiaryId),
            ["benefit_category"] = x.BenefitCategory,
            ["service_date"] = x.ServiceDate,
            ["quantity"] = x.Quantity,
            ["limit_value"] = null,
            ["consumed_value"] = x.Quantity,
            ["percent_used"] = null,
            ["utilization_band"] = null,
            ["provider_id"] = x.ProviderId,
            ["network_tier"] = null,
        })];
    }

    private async Task<List<Dictionary<string, object?>>> NetworkTiersAsync(
        ExtractFilter filter, PermittedPayers payers, CancellationToken ct)
    {
        var scopedPolicies = ScopedPolicies(payers);
        var planVersionIds = await db.PolicyPlans.AsNoTracking()
            .Where(pp => !pp.IsDeleted && scopedPolicies.Contains(pp.PolicyId))
            .Select(pp => new { pp.PlanVersionId, pp.PlanLabel })
            .ToListAsync(ct);
        var labels = planVersionIds
            .GroupBy(x => x.PlanVersionId)
            .ToDictionary(g => g.Key, g => string.Join(" / ", g.Select(x => x.PlanLabel).Distinct()));

        var versionIds = labels.Keys.ToList();
        var rules = await db.BenefitRules.AsNoTracking().Include(r => r.Tiers)
            .Where(r => versionIds.Contains(r.PlanVersionId))
            .Take(MaxRows + 1).ToListAsync(ct);
        var categories = await db.BenefitCategories.AsNoTracking().ToDictionaryAsync(c => c.BenefitCategoryId, c => c.Code, ct);

        var rows = new List<Dictionary<string, object?>>();
        foreach (var rule in rules)
        {
            var code = categories.GetValueOrDefault(rule.BenefitCategoryId);
            if (filter.BenefitCategory is { } wanted && !string.Equals(code, wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var tier in rule.Tiers)
            {
                if (filter.NetworkTier is { } tierCode
                    && !string.Equals(tier.TierCode, tierCode, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tier_code"] = tier.TierCode,
                    ["plan_label"] = labels.GetValueOrDefault(rule.PlanVersionId),
                    ["benefit_category"] = code,
                    ["is_covered"] = tier.IsCovered,
                    ["copay_fixed"] = tier.CopayFixed,
                    ["copay_percent"] = tier.CopayPercent,
                    ["coinsurance_percent"] = tier.CoinsurancePercent,
                    ["requires_preauth"] = tier.ResolvesPreauth(rule),
                });
            }
        }
        return rows;
    }

    // ---- Scope -------------------------------------------------------------------------------------------

    /// <summary>The payer restriction as a SUBQUERY over policy ids — a predicate inside the SQL, exactly as
    /// 19.5's queries apply it, rather than a filter over materialised results.</summary>
    private IQueryable<Guid> ScopedPolicies(PermittedPayers payers) =>
        ApplyPayerScope(db.Policies.AsNoTracking().Where(p => !p.IsDeleted), payers).Select(p => p.PolicyId);

    private static IQueryable<Domain.Policy> ApplyPayerScope(IQueryable<Domain.Policy> query, PermittedPayers payers)
    {
        if (payers.IsUnrestricted) return query;
        // A policy with NO payer is readable only by an unrestricted caller (ADR-0024): a restricted user asked
        // for one payer's book of business, and a row that might belong to any payer is not it.
        var ids = payers.PayerIds.ToList();
        return query.Where(p => p.PayerId != null && ids.Contains(p.PayerId.Value));
    }

    // ---- Serialization -----------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static byte[] Serialize(
        ExtractFormat format, IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string?>> rows, ExtractEntity entity) => format switch
        {
            ExtractFormat.Csv => BulkCsv.Write(columns, rows),
            ExtractFormat.Json => JsonSerializer.SerializeToUtf8Bytes(
                rows.Select(r => columns.Select((c, i) => (c, (object?)r[i])).ToDictionary(x => x.c, x => x.Item2)),
                JsonOptions),
            ExtractFormat.Xlsx => Xlsx(columns, rows, entity),
            _ => BulkCsv.Write(columns, rows),
        };

    private static byte[] Xlsx(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows, ExtractEntity entity)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(entity.ToString());
        for (var c = 0; c < columns.Count; c++) sheet.Cell(1, c + 1).Value = columns[c];
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < columns.Count; c++)
                // SetValue with a string, never a formula: a cell beginning '=' in a spreadsheet is executable,
                // and this file is opened in a spreadsheet by definition.
                sheet.Cell(r + 2, c + 1).SetValue(rows[r][c] ?? "");
        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static string Render(object? value) => value switch
    {
        null => "",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset dt => dt.ToString("O", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.###", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? "",
    };

    private static string FileName(ExtractRequest request, ExtractRun run) =>
        $"{request.Entity.ToString().ToLowerInvariant()}-{(run.AsOf is { } d ? $"asof-{d:yyyyMMdd}-" : "")}{run.RunId:N}" +
        request.Format switch { ExtractFormat.Xlsx => ".xlsx", ExtractFormat.Json => ".json", _ => ".csv" };

    private static string ContentType(ExtractFormat format) => format switch
    {
        ExtractFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ExtractFormat.Json => "application/json",
        _ => "text/csv",
    };
}

/// <summary>
/// The extract filter — the SAME vocabulary as 19.5's queries, plus the as-of date and a service-date window
/// that only the utilization entity uses.
/// </summary>
public sealed record ExtractFilter(
    Guid? PayerId = null,
    Guid? PolicyId = null,
    Guid? PolicyPlanId = null,
    string? PlanLabel = null,
    Guid? PlanVersionId = null,
    /// <summary>"The list as it stood on this date", reconstructed from effective dating + enrollment_event.</summary>
    DateOnly? AsOf = null,
    Guid? GroupId = null,
    string? NetworkTier = null,
    Guid? BranchId = null,
    EnrollmentStatus? MemberStatus = null,
    Relationship? Relationship = null,
    DateOnly? EnrolledFrom = null,
    DateOnly? EnrolledTo = null,
    WaitingPeriodState? WaitingPeriod = null,
    string? BenefitCategory = null,
    UtilizationBand? UtilizationBand = null,
    DateOnly? ServiceFrom = null,
    DateOnly? ServiceTo = null);

public sealed record ExtractRequest(
    ExtractEntity Entity, ExtractFilter Filter, IReadOnlyList<string>? Columns,
    ExtractFormat Format = ExtractFormat.Csv, Guid? DefinitionId = null);

public sealed record ExtractResult(
    ExtractRun? Run,
    IReadOnlyList<string> Columns,
    IReadOnlyList<WithheldColumn> Withheld,
    int RowCount,
    byte[]? Inline,
    Guid? DocumentId,
    string FileName,
    string ContentType,
    string? Refusal)
{
    public static ExtractResult Refused(string reason, IReadOnlyList<WithheldColumn> withheld) =>
        new(null, [], withheld, 0, null, null, "", "text/csv", reason);
}
