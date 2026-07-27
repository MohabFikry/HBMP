using System.Globalization;
using Mersal.Authz;
using Mersal.Policy.Domain;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Infrastructure;

// Phase 19.5b — one applier per job type. Each one RESOLVES the row's references, checks scope, and then hands
// the change to the code that already owns it: MembershipCommands for memberships, the owning service's API
// for contacts and tier assignments.
//
// VALIDATION IS RE-RUN AT COMMIT. Validation and commit are separated in time — that separation is the whole
// point of the dry run — and the world moves in between. A group is closed, a policy expires, a plan fills up.
// Applying a row on the strength of a check made yesterday is how a bulk file writes something the system
// would refuse today.

/// <summary>What a row is being applied under: who, with which token, and inside which scope.</summary>
public sealed class BulkScope
{
    public required ActorRef Actor { get; init; }
    public string? BearerToken { get; init; }
    public required PermittedPayers Payers { get; init; }

    /// <summary>Null = the caller is not branch-restricted. Empty = restricted to nothing, which denies.</summary>
    public IReadOnlySet<Guid>? PermittedBranchIds { get; init; }
    public Guid? ActiveBranchId { get; init; }
    public bool MaySupervise { get; init; }

    /// <summary>Per-job memo of beneficiary statuses. A 10 000-row file would otherwise make 10 000 calls to
    /// patient-service to ask the same question about the same few hundred people.</summary>
    public Dictionary<Guid, string?> BeneficiaryStatuses { get; } = [];

    public bool BranchAllows(Guid? branchId) =>
        PermittedBranchIds is null || (branchId is { } b && PermittedBranchIds.Contains(b));
}

public sealed record RowPreview(string SummaryEn, string SummaryAr, IReadOnlyDictionary<string, object?> Changes)
{
    public static RowPreview Of(string en, string ar, params (string Key, object? Value)[] changes) =>
        new(en, ar, changes.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal));
}

public abstract record RowOutcome
{
    public sealed record Valid(object Normalized, RowPreview Preview) : RowOutcome;
    public sealed record Applied(Guid? TargetRef, object? Before, RowPreview Preview) : RowOutcome;
    public sealed record Invalid(RowError Error) : RowOutcome;
    /// <summary>Already done — an idempotent re-commit walking past a row it wrote before. Deliberately not
    /// Failed: a resumed job would otherwise report a failure for every row it correctly skipped.</summary>
    public sealed record Skipped(string Reason) : RowOutcome;
}

public interface IBulkRowApplier
{
    BulkJobType JobType { get; }

    Task<RowOutcome> ValidateAsync(ParsedRow row, BulkScope scope, CancellationToken ct = default);

    Task<RowOutcome> ApplyAsync(ParsedRow row, BulkScope scope, Guid jobId, int rowNumber, CancellationToken ct = default);

    /// <summary>Whether this job type can be reversed at all, and how. A type that cannot be reversed says so
    /// up front rather than failing halfway through a rollback somebody has already announced.</summary>
    bool IsReversible { get; }

    Task<RowOutcome> ReverseAsync(BulkJobRow row, BulkScope scope, CancellationToken ct = default);
}

/// <summary>Shared cell reading + reference resolution. Every applier needs the same five lookups, and five
/// slightly different versions of "find the policy by number" is how two job types come to disagree about
/// whether a closed policy counts.</summary>
internal static class BulkResolve
{
    public static RowError? Required(ParsedRow row, string column, out string value)
    {
        value = row.Text(column) ?? "";
        return string.IsNullOrWhiteSpace(value) ? RowError.MissingColumn(column) : null;
    }

    public static RowError? Date(ParsedRow row, string column, bool required, out DateOnly? value)
    {
        value = null;
        var raw = row.Text(column);
        if (string.IsNullOrWhiteSpace(raw)) return required ? RowError.MissingColumn(column) : null;
        if (!BulkCells.TryDate(raw, out var parsed)) return RowError.BadFormat(column, "date (yyyy-MM-dd)");
        value = parsed;
        return null;
    }

    public static RowError? Guid(ParsedRow row, string column, bool required, out Guid? value)
    {
        value = null;
        var raw = row.Text(column);
        if (string.IsNullOrWhiteSpace(raw)) return required ? RowError.MissingColumn(column) : null;
        if (!BulkCells.TryGuid(raw, out var parsed)) return RowError.BadFormat(column, "identifier");
        value = parsed;
        return null;
    }

    public static async Task<(Domain.Policy? Policy, RowError? Error)> PolicyByNoAsync(
        PolicyDbContext db, string policyNo, BulkScope scope, CancellationToken ct)
    {
        var policy = await db.Policies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PolicyNo == policyNo && !p.IsDeleted, ct);
        if (policy is null) return (null, RowError.Unknown("Policy", policyNo));

        // Payer scope, PER ROW. A bulk file is exactly the shape an attempt to reach outside one's own book of
        // business would take: one line among ten thousand, invisible in any summary.
        if (PayerScopeRules.Check(scope.Payers, policy.PayerId) == PayerScopeOutcome.Denied)
            return (null, RowError.OutOfScope($"policy {policyNo}"));
        return (policy, null);
    }

    public static async Task<(Enrollment? Enrollment, RowError? Error)> MemberByNoAsync(
        PolicyDbContext db, string memberNo, BulkScope scope, CancellationToken ct)
    {
        var member = await db.Enrollments
            .FirstOrDefaultAsync(e => e.MemberNo == memberNo && !e.IsDeleted, ct);
        if (member is null) return (null, RowError.Unknown("Member", memberNo));

        var payerId = await db.Policies.AsNoTracking()
            .Where(p => p.PolicyId == member.PolicyId).Select(p => p.PayerId).FirstOrDefaultAsync(ct);
        if (PayerScopeRules.Check(scope.Payers, payerId == System.Guid.Empty ? null : payerId) == PayerScopeOutcome.Denied)
            return (null, RowError.OutOfScope($"member {memberNo}"));
        return (member, null);
    }

    public static async Task<(PolicyPlan? Plan, RowError? Error)> PlanByLabelAsync(
        PolicyDbContext db, Guid policyId, string label, CancellationToken ct)
    {
        var matches = await db.PolicyPlans.AsNoTracking()
            .Where(pp => pp.PolicyId == policyId && !pp.IsDeleted && EF.Functions.ILike(pp.PlanLabel, label))
            .ToListAsync(ct);
        return matches.Count switch
        {
            0 => (null, RowError.Unknown("Plan", label)),
            1 => (matches[0], null),
            // Labels are not unique by constraint (a closed plan may share a label with its successor). An
            // ambiguous label must not be resolved by picking one — that is a coin toss over entitlement.
            _ => (null, RowError.Rule("AMBIGUOUS_PLAN",
                $"'{label}' matches {matches.Count} plans on this policy; name the plan more precisely.",
                $"الاسم '{label}' يطابق {matches.Count} خطط في هذه الوثيقة؛ يُرجى التحديد بدقة.")),
        };
    }

    public static async Task<(MemberGroup? Group, RowError? Error)> GroupByCodeAsync(
        PolicyDbContext db, Guid policyId, string code, CancellationToken ct)
    {
        var group = await db.MemberGroups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.PolicyId == policyId && g.GroupCode == code && !g.IsDeleted, ct);
        return group is null ? (null, RowError.Unknown("Group", code)) : (group, null);
    }

    /// <summary>Ask patient-service once per beneficiary per job, not once per row.</summary>
    public static async Task<string?> BeneficiaryStatusAsync(
        IBeneficiaryStatusProbe probe, Guid beneficiaryId, BulkScope scope, CancellationToken ct)
    {
        if (scope.BeneficiaryStatuses.TryGetValue(beneficiaryId, out var cached)) return cached;
        var status = await probe.GetStatusAsync(beneficiaryId, scope.BearerToken, ct);
        scope.BeneficiaryStatuses[beneficiaryId] = status;
        return status;
    }

    /// <summary>Translate a membership failure into a row error. The SAME code and the SAME sentence the single
    /// -member form would have shown, so an operator who hits a rule in bulk and then in the UI is not told two
    /// different things about one rule.</summary>
    public static RowError FromMembership(MembershipError error) => new(
        error.Code,
        error.Failures is { Count: > 0 } f ? $"{error.Detail} ({string.Join("; ", f)})" : error.Detail,
        error.Detail);
}

// ============================================================================================================
// MemberEnrolment
// ============================================================================================================

public sealed class MemberEnrolmentApplier(
    PolicyDbContext db, MembershipCommands membership, IBeneficiaryStatusProbe beneficiaries) : IBulkRowApplier
{
    public BulkJobType JobType => BulkJobType.MemberEnrolment;
    public bool IsReversible => true;

    public async Task<RowOutcome> ValidateAsync(ParsedRow row, BulkScope scope, CancellationToken ct = default)
    {
        var (command, preview, error) = await ResolveAsync(row, scope, ct);
        return error is not null ? new RowOutcome.Invalid(error) : new RowOutcome.Valid(command!, preview!);
    }

    public async Task<RowOutcome> ApplyAsync(
        ParsedRow row, BulkScope scope, Guid jobId, int rowNumber, CancellationToken ct = default)
    {
        var (command, preview, error) = await ResolveAsync(row, scope, ct);
        if (error is not null) return new RowOutcome.Invalid(error);

        var key = BulkIdempotency.KeyFor(jobId, rowNumber);
        var result = await membership.EnrollAsync(command!, key, scope.BearerToken, scope.Actor, ct);
        if (!result.Ok) return new RowOutcome.Invalid(BulkResolve.FromMembership(result.Error!));

        var outcome = result.Value!;
        return outcome.WasReplay
            ? new RowOutcome.Skipped($"already applied as {outcome.Enrollment.MemberNo}")
            : new RowOutcome.Applied(outcome.Enrollment.EnrollmentId, null, preview! with
            {
                SummaryEn = $"{preview!.SummaryEn} → {outcome.Enrollment.MemberNo}",
            });
    }

    public async Task<RowOutcome> ReverseAsync(BulkJobRow row, BulkScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.TargetRef is not { } enrollmentId) return new RowOutcome.Skipped("nothing was applied");

        // THE ROLLBACK GUARD. Once a member has consumed benefit under this membership, cancelling it would
        // remove the coverage the consumption points at — leaving a dispensed prescription or a completed
        // investigation attached to an entitlement the record no longer admits to. The fix from there is a
        // termination and a claims adjustment, not an erasure, and saying so is more use than refusing quietly.
        var consumed = await db.Coverages.AsNoTracking()
            .Where(c => c.EnrollmentId == enrollmentId)
            .SelectMany(c => c.Limits)
            .AnyAsync(l => l.ConsumedValue > 0m, ct);
        if (consumed)
            return new RowOutcome.Invalid(RowError.Rule("CONSUMPTION_EXISTS",
                "This member has already consumed benefit under the membership this row created, so it cannot be " +
                "rolled back. Terminate the membership instead, and raise a claims adjustment if needed.",
                "استهلك هذا العضو منافع بالفعل تحت العضوية التي أنشأها هذا الصف، لذا لا يمكن التراجع عنها. " +
                "يُرجى إنهاء العضوية بدلًا من ذلك، مع تسوية للمطالبات عند اللزوم."));

        var result = await membership.CancelAsync(enrollmentId, "bulk job rolled back", scope.Actor, ct);
        return result.Ok
            ? new RowOutcome.Applied(enrollmentId, null, RowPreview.Of(
                "Membership cancelled", "تم إلغاء العضوية", ("enrollmentId", enrollmentId)))
            : new RowOutcome.Invalid(BulkResolve.FromMembership(result.Error!));
    }

    private async Task<(EnrollCommand? Command, RowPreview? Preview, RowError? Error)> ResolveAsync(
        ParsedRow row, BulkScope scope, CancellationToken ct)
    {
        if (BulkResolve.Guid(row, "beneficiary_id", true, out var beneficiaryId) is { } e1) return (null, null, e1);
        if (BulkResolve.Required(row, "policy_no", out var policyNo) is { } e2) return (null, null, e2);
        if (BulkResolve.Required(row, "relationship", out var relationship) is { } e3) return (null, null, e3);
        if (BulkResolve.Date(row, "effective_from", true, out var effectiveFrom) is { } e4) return (null, null, e4);
        if (BulkResolve.Date(row, "effective_to", false, out var effectiveTo) is { } e5) return (null, null, e5);
        if (BulkResolve.Guid(row, "branch_id", false, out var branchId) is { } e6) return (null, null, e6);

        if (!Enum.TryParse<Relationship>(relationship, ignoreCase: true, out _))
            return (null, null, RowError.BadFormat("relationship", "relationship (Principal, Spouse, Child, Dependent)"));

        int? age = null;
        if (row.Text("age_years") is { } rawAge)
        {
            if (!BulkCells.TryInt(rawAge, out var parsedAge)) return (null, null, RowError.BadFormat("age_years", "whole number"));
            age = parsedAge;
        }

        var (policy, policyError) = await BulkResolve.PolicyByNoAsync(db, policyNo, scope, ct);
        if (policyError is not null) return (null, null, policyError);

        Guid? planId = null;
        if (row.Text("plan_label") is { } label)
        {
            var (plan, planError) = await BulkResolve.PlanByLabelAsync(db, policy!.PolicyId, label, ct);
            if (planError is not null) return (null, null, planError);
            planId = plan!.PolicyPlanId;
        }

        Guid? groupId = null;
        if (row.Text("group_code") is { } groupCode)
        {
            var (group, groupError) = await BulkResolve.GroupByCodeAsync(db, policy!.PolicyId, groupCode, ct);
            if (groupError is not null) return (null, null, groupError);
            groupId = group!.GroupId;
        }

        Guid? principalEnrollmentId = null;
        if (row.Text("principal_member_no") is { } principalNo)
        {
            var (principal, principalError) = await BulkResolve.MemberByNoAsync(db, principalNo, scope, ct);
            if (principalError is not null) return (null, null, principalError);
            principalEnrollmentId = principal!.EnrollmentId;
        }

        // Branch scope. A branch-restricted caller cannot enrol into a branch they are not permitted; and if
        // they name none, the row inherits their ACTIVE branch rather than landing as NULL — a bulk file must
        // not be the way rows arrive with no branch at all once branch scoping is in force for the submitter.
        if (scope.PermittedBranchIds is not null)
        {
            branchId ??= scope.ActiveBranchId;
            if (!scope.BranchAllows(branchId))
                return (null, null, RowError.OutOfScope($"branch {branchId?.ToString() ?? "(none)"}"));
        }

        // A beneficiary who is not Active fails HERE, in the dry run, rather than at commit. This is the single
        // most common reason a real enrolment file has invalid rows, and finding it in the preview is the
        // difference between fixing 37 rows and re-running a 10 000-row job.
        var status = await BulkResolve.BeneficiaryStatusAsync(beneficiaries, beneficiaryId!.Value, scope, ct);
        if (status is null)
            return (null, null, RowError.Unknown("Beneficiary", beneficiaryId.Value.ToString()));
        if (!string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
            return (null, null, RowError.Rule("BENEFICIARY_NOT_ACTIVE",
                $"The beneficiary is {status}; only an Active beneficiary can be enrolled.",
                $"حالة المستفيد {status}؛ لا يمكن تسجيل سوى مستفيد نشط."));

        var command = new EnrollCommand(
            beneficiaryId.Value, policy!.PolicyId, planId, groupId, relationship,
            principalEnrollmentId, effectiveFrom!.Value, effectiveTo, branchId, age);

        var preview = RowPreview.Of(
            $"Enrol {beneficiaryId} on policy {policyNo} from {effectiveFrom:yyyy-MM-dd}",
            $"تسجيل {beneficiaryId} في الوثيقة {policyNo} اعتبارًا من {effectiveFrom:yyyy-MM-dd}",
            ("policyNo", policyNo), ("planLabel", row.Text("plan_label")), ("groupCode", row.Text("group_code")),
            ("relationship", relationship), ("effectiveFrom", effectiveFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("branchId", branchId));

        return (command, preview, null);
    }
}

// ============================================================================================================
// MemberTermination
// ============================================================================================================

public sealed class MemberTerminationApplier(
    PolicyDbContext db, MembershipCommands membership, IBusinessCalendar calendar) : IBulkRowApplier
{
    public BulkJobType JobType => BulkJobType.MemberTermination;
    public bool IsReversible => true;

    public async Task<RowOutcome> ValidateAsync(ParsedRow row, BulkScope scope, CancellationToken ct = default)
    {
        var (resolved, preview, error) = await ResolveAsync(row, scope, ct);
        return error is not null ? new RowOutcome.Invalid(error) : new RowOutcome.Valid(resolved!, preview!);
    }

    public async Task<RowOutcome> ApplyAsync(
        ParsedRow row, BulkScope scope, Guid jobId, int rowNumber, CancellationToken ct = default)
    {
        var (resolved, preview, error) = await ResolveAsync(row, scope, ct);
        if (error is not null) return new RowOutcome.Invalid(error);

        var (enrollmentId, effectiveDate, reason, before) = resolved!.Value;
        // Idempotency for a lifecycle change is STATE, not a key: a membership already terminated to the same
        // date is the outcome this row asked for, so a re-commit skips rather than 409-ing on every row.
        var current = await db.Enrollments.AsNoTracking().FirstOrDefaultAsync(e => e.EnrollmentId == enrollmentId, ct);
        if (current is { Status: EnrollmentStatus.Terminated } && current.EffectiveTo == effectiveDate)
            return new RowOutcome.Skipped("already terminated to this date");

        var result = await membership.TerminateAsync(enrollmentId, effectiveDate, reason, scope.MaySupervise, scope.Actor, ct);
        return result.Ok
            ? new RowOutcome.Applied(enrollmentId, before, preview!)
            : new RowOutcome.Invalid(BulkResolve.FromMembership(result.Error!));
    }

    public async Task<RowOutcome> ReverseAsync(BulkJobRow row, BulkScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.TargetRef is not { } enrollmentId) return new RowOutcome.Skipped("nothing was applied");

        // The Cairo business date (18.A3), not a UTC instant: a rollback run at 01:00 Cairo would otherwise
        // take effect on yesterday's date.
        var result = await membership.ReinstateAsync(
            enrollmentId, calendar.Today(), "bulk job rolled back", scope.Actor, ct);
        return result.Ok
            ? new RowOutcome.Applied(enrollmentId, null, RowPreview.Of(
                "Membership reinstated", "تمت إعادة العضوية", ("enrollmentId", enrollmentId)))
            : new RowOutcome.Invalid(BulkResolve.FromMembership(result.Error!));
    }

    private async Task<((Guid EnrollmentId, DateOnly EffectiveDate, string Reason, object Before)? Resolved,
        RowPreview? Preview, RowError? Error)> ResolveAsync(ParsedRow row, BulkScope scope, CancellationToken ct)
    {
        if (BulkResolve.Required(row, "member_no", out var memberNo) is { } e1) return (null, null, e1);
        if (BulkResolve.Date(row, "effective_date", true, out var effectiveDate) is { } e2) return (null, null, e2);
        if (BulkResolve.Required(row, "reason", out var reason) is { } e3) return (null, null, e3);

        var (member, memberError) = await BulkResolve.MemberByNoAsync(db, memberNo, scope, ct);
        if (memberError is not null) return (null, null, memberError);

        var before = new { status = member!.Status.ToString(), effectiveTo = member.EffectiveTo };
        var preview = RowPreview.Of(
            $"Terminate {memberNo} on {effectiveDate:yyyy-MM-dd}",
            $"إنهاء عضوية {memberNo} بتاريخ {effectiveDate:yyyy-MM-dd}",
            ("memberNo", memberNo), ("from", member.Status.ToString()), ("to", "Terminated"),
            ("effectiveDate", effectiveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        return ((member.EnrollmentId, effectiveDate!.Value, reason, before), preview, null);
    }
}

// ============================================================================================================
// PlanChange
// ============================================================================================================

public sealed class PlanChangeApplier(
    PolicyDbContext db, MembershipCommands membership, IBusinessCalendar calendar) : IBulkRowApplier
{
    public BulkJobType JobType => BulkJobType.PlanChange;
    public bool IsReversible => true;

    public async Task<RowOutcome> ValidateAsync(ParsedRow row, BulkScope scope, CancellationToken ct = default)
    {
        var (resolved, preview, error) = await ResolveAsync(row, scope, ct);
        return error is not null ? new RowOutcome.Invalid(error) : new RowOutcome.Valid(resolved!, preview!);
    }

    public async Task<RowOutcome> ApplyAsync(
        ParsedRow row, BulkScope scope, Guid jobId, int rowNumber, CancellationToken ct = default)
    {
        var (resolved, preview, error) = await ResolveAsync(row, scope, ct);
        if (error is not null) return new RowOutcome.Invalid(error);

        var (enrollmentId, policyPlanId, effectiveDate, reason, before) = resolved!.Value;
        var current = await db.Enrollments.AsNoTracking().FirstOrDefaultAsync(e => e.EnrollmentId == enrollmentId, ct);
        if (current?.PolicyPlanId == policyPlanId) return new RowOutcome.Skipped("already on this plan");

        var result = await membership.ChangePlanAsync(enrollmentId, policyPlanId, effectiveDate, reason, scope.Actor, ct);
        return result.Ok
            ? new RowOutcome.Applied(enrollmentId, before, preview!)
            : new RowOutcome.Invalid(BulkResolve.FromMembership(result.Error!));
    }

    public async Task<RowOutcome> ReverseAsync(BulkJobRow row, BulkScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.TargetRef is not { } enrollmentId) return new RowOutcome.Skipped("nothing was applied");

        var before = BulkSnapshots.Read(row.BeforeSnapshot);
        if (before is null || !before.TryGetValue("policyPlanId", out var raw) || !Guid.TryParse(raw?.ToString(), out var previousPlan))
            return new RowOutcome.Invalid(RowError.Rule("NO_PRIOR_PLAN",
                "The plan this member was on before the change was not recorded, so the change cannot be reversed automatically.",
                "لم تُسجَّل الخطة السابقة لهذا العضو، لذا يتعذّر التراجع تلقائيًا عن التغيير."));

        // The same consumption guard as an enrolment rollback, for a sharper reason: a plan change CARRIES the
        // accumulator forward (ADR-0020). Reversing it after further consumption would carry it back again and
        // count the same spend twice.
        var consumed = await db.Coverages.AsNoTracking()
            .Where(c => c.EnrollmentId == enrollmentId && !c.IsDeleted)
            .SelectMany(c => c.Limits).AnyAsync(l => l.ConsumedValue > 0m, ct);
        if (consumed)
            return new RowOutcome.Invalid(RowError.Rule("CONSUMPTION_EXISTS",
                "Benefit has been consumed since this plan change, so reversing it would double-count that spend. " +
                "Move the member back with a fresh plan change instead.",
                "تم استهلاك منافع منذ تغيير الخطة، والتراجع سيؤدي إلى احتساب الاستهلاك مرتين. " +
                "يُرجى إعادة العضو عبر تغيير خطة جديد."));

        var result = await membership.ChangePlanAsync(
            enrollmentId, previousPlan, calendar.Today(), "bulk job rolled back", scope.Actor, ct);
        return result.Ok
            ? new RowOutcome.Applied(enrollmentId, null, RowPreview.Of(
                "Plan change reversed", "تم التراجع عن تغيير الخطة", ("policyPlanId", previousPlan)))
            : new RowOutcome.Invalid(BulkResolve.FromMembership(result.Error!));
    }

    private async Task<((Guid EnrollmentId, Guid PolicyPlanId, DateOnly EffectiveDate, string Reason, object Before)? Resolved,
        RowPreview? Preview, RowError? Error)> ResolveAsync(ParsedRow row, BulkScope scope, CancellationToken ct)
    {
        if (BulkResolve.Required(row, "member_no", out var memberNo) is { } e1) return (null, null, e1);
        if (BulkResolve.Required(row, "plan_label", out var label) is { } e2) return (null, null, e2);
        if (BulkResolve.Date(row, "effective_date", true, out var effectiveDate) is { } e3) return (null, null, e3);
        if (BulkResolve.Required(row, "reason", out var reason) is { } e4) return (null, null, e4);

        var (member, memberError) = await BulkResolve.MemberByNoAsync(db, memberNo, scope, ct);
        if (memberError is not null) return (null, null, memberError);

        var (plan, planError) = await BulkResolve.PlanByLabelAsync(db, member!.PolicyId, label, ct);
        if (planError is not null) return (null, null, planError);

        var before = new { policyPlanId = member.PolicyPlanId };
        var preview = RowPreview.Of(
            $"Move {memberNo} to plan '{label}' from {effectiveDate:yyyy-MM-dd}",
            $"نقل {memberNo} إلى خطة '{label}' اعتبارًا من {effectiveDate:yyyy-MM-dd}",
            ("memberNo", memberNo), ("from", member.PolicyPlanId), ("to", plan!.PolicyPlanId),
            ("effectiveDate", effectiveDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        return ((member.EnrollmentId, plan.PolicyPlanId, effectiveDate!.Value, reason, before), preview, null);
    }
}

// ============================================================================================================
// GroupAssignment
// ============================================================================================================

public sealed class GroupAssignmentApplier(
    PolicyDbContext db, MembershipCommands membership, IBusinessCalendar calendar) : IBulkRowApplier
{
    public BulkJobType JobType => BulkJobType.GroupAssignment;
    public bool IsReversible => true;

    public async Task<RowOutcome> ValidateAsync(ParsedRow row, BulkScope scope, CancellationToken ct = default)
    {
        var (resolved, preview, error) = await ResolveAsync(row, scope, ct);
        return error is not null ? new RowOutcome.Invalid(error) : new RowOutcome.Valid(resolved!, preview!);
    }

    public async Task<RowOutcome> ApplyAsync(
        ParsedRow row, BulkScope scope, Guid jobId, int rowNumber, CancellationToken ct = default)
    {
        var (resolved, preview, error) = await ResolveAsync(row, scope, ct);
        if (error is not null) return new RowOutcome.Invalid(error);

        var (enrollmentId, groupId, effectiveDate, reason, before) = resolved!.Value;
        var current = await db.Enrollments.AsNoTracking().FirstOrDefaultAsync(e => e.EnrollmentId == enrollmentId, ct);
        if (current?.GroupId == groupId) return new RowOutcome.Skipped("already in this group");

        var result = await membership.ChangeGroupAsync(enrollmentId, groupId, effectiveDate, reason, scope.Actor, ct);
        return result.Ok
            ? new RowOutcome.Applied(enrollmentId, before, preview!)
            : new RowOutcome.Invalid(BulkResolve.FromMembership(result.Error!));
    }

    public async Task<RowOutcome> ReverseAsync(BulkJobRow row, BulkScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.TargetRef is not { } enrollmentId) return new RowOutcome.Skipped("nothing was applied");

        var before = BulkSnapshots.Read(row.BeforeSnapshot);
        Guid? previousGroup = null;
        if (before is not null && before.TryGetValue("groupId", out var raw) && Guid.TryParse(raw?.ToString(), out var g))
            previousGroup = g;

        var result = await membership.ChangeGroupAsync(
            enrollmentId, previousGroup, calendar.Today(), "bulk job rolled back", scope.Actor, ct);
        return result.Ok
            ? new RowOutcome.Applied(enrollmentId, null, RowPreview.Of(
                "Group restored", "تمت إعادة المجموعة السابقة", ("groupId", previousGroup)))
            : new RowOutcome.Invalid(BulkResolve.FromMembership(result.Error!));
    }

    private async Task<((Guid EnrollmentId, Guid? GroupId, DateOnly EffectiveDate, string? Reason, object Before)? Resolved,
        RowPreview? Preview, RowError? Error)> ResolveAsync(ParsedRow row, BulkScope scope, CancellationToken ct)
    {
        if (BulkResolve.Required(row, "member_no", out var memberNo) is { } e1) return (null, null, e1);
        if (BulkResolve.Date(row, "effective_date", true, out var effectiveDate) is { } e2) return (null, null, e2);

        var (member, memberError) = await BulkResolve.MemberByNoAsync(db, memberNo, scope, ct);
        if (memberError is not null) return (null, null, memberError);

        Guid? groupId = null;
        var code = row.Text("group_code");
        if (code is not null)
        {
            var (group, groupError) = await BulkResolve.GroupByCodeAsync(db, member!.PolicyId, code, ct);
            if (groupError is not null) return (null, null, groupError);
            groupId = group!.GroupId;
        }

        var before = new { groupId = member!.GroupId };
        var preview = RowPreview.Of(
            code is null ? $"Remove {memberNo} from their group" : $"Move {memberNo} to group '{code}'",
            code is null ? $"إخراج {memberNo} من مجموعته" : $"نقل {memberNo} إلى المجموعة '{code}'",
            ("memberNo", memberNo), ("from", member.GroupId), ("to", groupId));

        return ((member.EnrollmentId, groupId, effectiveDate!.Value, row.Text("reason"), before), preview, null);
    }
}

// ============================================================================================================
// ContactUpdate — patient-service owns the field, so patient-service does the write
// ============================================================================================================

public sealed class ContactUpdateApplier(IBeneficiaryContactWriter contacts) : IBulkRowApplier
{
    public BulkJobType JobType => BulkJobType.ContactUpdate;
    public bool IsReversible => true;

    public Task<RowOutcome> ValidateAsync(ParsedRow row, BulkScope scope, CancellationToken ct = default)
    {
        var (resolved, preview, error) = Resolve(row);
        return Task.FromResult<RowOutcome>(
            error is not null ? new RowOutcome.Invalid(error) : new RowOutcome.Valid(resolved!, preview!));
    }

    public async Task<RowOutcome> ApplyAsync(
        ParsedRow row, BulkScope scope, Guid jobId, int rowNumber, CancellationToken ct = default)
    {
        var (resolved, preview, error) = Resolve(row);
        if (error is not null) return new RowOutcome.Invalid(error);

        ArgumentNullException.ThrowIfNull(scope);
        var (beneficiaryId, contactType, value, isPrimary, channel) = resolved!.Value;
        var result = await contacts.UpsertAsync(beneficiaryId, contactType, value, isPrimary, channel, scope.BearerToken, ct);
        return result switch
        {
            ContactWriteResult.Written w => new RowOutcome.Applied(
                beneficiaryId, w.Previous is null ? null : new { previous = w.Previous }, preview!),
            ContactWriteResult.NotFound => new RowOutcome.Invalid(RowError.Unknown("Beneficiary", beneficiaryId.ToString())),
            ContactWriteResult.Rejected r => new RowOutcome.Invalid(RowError.Rule("CONTACT_REJECTED", r.Detail, r.Detail)),
            // Not a row failure the operator can fix — the owning service could not be reached. Reported as
            // such so a retry of the job is the obvious next step rather than an edit to a correct row.
            _ => new RowOutcome.Invalid(RowError.Rule("PATIENT_SERVICE_UNAVAILABLE",
                "patient-service could not be reached; this row was not attempted. Retry the job.",
                "تعذّر الوصول إلى خدمة المرضى؛ لم تتم محاولة تنفيذ هذا الصف. يُرجى إعادة تشغيل الوظيفة.")),
        };
    }

    public async Task<RowOutcome> ReverseAsync(BulkJobRow row, BulkScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(scope);
        if (row.TargetRef is not { } beneficiaryId) return new RowOutcome.Skipped("nothing was applied");

        var before = BulkSnapshots.Read(row.BeforeSnapshot);
        if (before is null || !before.TryGetValue("previous", out var previous) || previous is null)
            // The row ADDED a contact that did not exist. Restoring "no contact" would mean deleting somebody's
            // phone number on the strength of a rollback, and a wrong extra number is less harmful than a
            // missing one.
            return new RowOutcome.Skipped("the row added a new contact; nothing to restore");

        var snapshot = BulkSnapshots.ReadContact(previous);
        if (snapshot is null) return new RowOutcome.Skipped("the prior contact could not be read");

        var result = await contacts.UpsertAsync(
            beneficiaryId, snapshot.ContactType, snapshot.Value, snapshot.IsPrimary, snapshot.PreferredChannel,
            scope.BearerToken, ct);
        return result is ContactWriteResult.Written
            ? new RowOutcome.Applied(beneficiaryId, null, RowPreview.Of(
                "Contact restored", "تمت استعادة بيانات الاتصال", ("beneficiaryId", beneficiaryId)))
            : new RowOutcome.Invalid(RowError.Rule("RESTORE_FAILED",
                "The prior contact could not be restored.", "تعذّرت استعادة بيانات الاتصال السابقة."));
    }

    private static ((Guid BeneficiaryId, string ContactType, string Value, bool IsPrimary, string? Channel)? Resolved,
        RowPreview? Preview, RowError? Error) Resolve(ParsedRow row)
    {
        if (BulkResolve.Guid(row, "beneficiary_id", true, out var beneficiaryId) is { } e1) return (null, null, e1);
        if (BulkResolve.Required(row, "contact_type", out var contactType) is { } e2) return (null, null, e2);
        if (BulkResolve.Required(row, "value", out var value) is { } e3) return (null, null, e3);

        if (contactType is not ("Phone" or "Email" or "Address" or "EmergencyContact"))
            return (null, null, RowError.BadFormat("contact_type", "contact type (Phone, Email, Address, EmergencyContact)"));

        var isPrimary = false;
        if (row.Text("is_primary") is { } rawPrimary && !BulkCells.TryBool(rawPrimary, out isPrimary))
            return (null, null, RowError.BadFormat("is_primary", "yes/no"));

        var preview = RowPreview.Of(
            $"Set {contactType} for {beneficiaryId}",
            $"تحديث {contactType} للمستفيد {beneficiaryId}",
            ("beneficiaryId", beneficiaryId), ("contactType", contactType), ("isPrimary", isPrimary));

        return ((beneficiaryId!.Value, contactType, value, isPrimary, row.Text("preferred_channel")), preview, null);
    }
}

// ============================================================================================================
// ProviderTierAssignment — provider-service owns the network
// ============================================================================================================

public sealed class ProviderTierAssignmentApplier(INetworkTierAssignmentWriter tiers) : IBulkRowApplier
{
    public BulkJobType JobType => BulkJobType.ProviderTierAssignment;
    public bool IsReversible => true;

    public async Task<RowOutcome> ValidateAsync(ParsedRow row, BulkScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var (resolved, preview, error) = Resolve(row);
        if (error is not null) return new RowOutcome.Invalid(error);

        var tierId = await tiers.ResolveTierAsync(resolved!.Value.TierCode, scope.BearerToken, ct);
        if (tierId is null)
            return new RowOutcome.Invalid(RowError.Unknown("Network tier", resolved.Value.TierCode));
        return new RowOutcome.Valid(resolved, preview!);
    }

    public async Task<RowOutcome> ApplyAsync(
        ParsedRow row, BulkScope scope, Guid jobId, int rowNumber, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var (resolved, preview, error) = Resolve(row);
        if (error is not null) return new RowOutcome.Invalid(error);

        var (tierCode, scopeType, scopeRef, effectiveFrom, effectiveTo) = resolved!.Value;
        var tierId = await tiers.ResolveTierAsync(tierCode, scope.BearerToken, ct);
        if (tierId is null) return new RowOutcome.Invalid(RowError.Unknown("Network tier", tierCode));

        var result = await tiers.AssignAsync(tierId.Value, scopeType, scopeRef, effectiveFrom, effectiveTo, scope.BearerToken, ct);
        return result switch
        {
            TierAssignmentResult.Assigned a => new RowOutcome.Applied(
                a.AssignmentId, new { tierCode, scopeType, scopeRef }, preview!),
            TierAssignmentResult.Rejected r => new RowOutcome.Invalid(RowError.Rule(r.Code, r.Detail, r.Detail)),
            _ => new RowOutcome.Invalid(RowError.Rule("PROVIDER_SERVICE_UNAVAILABLE",
                "provider-service could not be reached; this row was not attempted. Retry the job.",
                "تعذّر الوصول إلى خدمة مقدمي الخدمة؛ لم تتم محاولة تنفيذ هذا الصف. يُرجى إعادة تشغيل الوظيفة.")),
        };
    }

    public async Task<RowOutcome> ReverseAsync(BulkJobRow row, BulkScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(scope);
        if (row.TargetRef is not { } assignmentId) return new RowOutcome.Skipped("nothing was applied");

        // CORRECT, not close. A rolled-back row is one that should never have been applied, so the assignment
        // is retroactively voided rather than ended today — otherwise the days between the upload and the
        // rollback stay priced at a tier nobody intended. provider-service refuses the correction if a claim
        // has already adjudicated against it, and THAT is the real guard.
        var result = await tiers.WithdrawAsync(assignmentId, "bulk job rolled back", correct: true, scope.BearerToken, ct);
        return result switch
        {
            TierAssignmentResult.Assigned => new RowOutcome.Applied(assignmentId, null, RowPreview.Of(
                "Tier assignment corrected", "تم تصحيح إسناد الشريحة", ("assignmentId", assignmentId))),
            TierAssignmentResult.Rejected r => new RowOutcome.Invalid(RowError.Rule(r.Code, r.Detail, r.Detail)),
            _ => new RowOutcome.Invalid(RowError.Rule("PROVIDER_SERVICE_UNAVAILABLE",
                "provider-service could not be reached.", "تعذّر الوصول إلى خدمة مقدمي الخدمة.")),
        };
    }

    private static ((string TierCode, string ScopeType, Guid ScopeRef, DateOnly EffectiveFrom, DateOnly? EffectiveTo)? Resolved,
        RowPreview? Preview, RowError? Error) Resolve(ParsedRow row)
    {
        if (BulkResolve.Required(row, "tier_code", out var tierCode) is { } e1) return (null, null, e1);
        if (BulkResolve.Required(row, "scope_type", out var scopeType) is { } e2) return (null, null, e2);
        if (BulkResolve.Guid(row, "scope_id", true, out var scopeRef) is { } e3) return (null, null, e3);
        if (BulkResolve.Date(row, "effective_from", true, out var effectiveFrom) is { } e4) return (null, null, e4);
        if (BulkResolve.Date(row, "effective_to", false, out var effectiveTo) is { } e5) return (null, null, e5);

        if (scopeType is not ("Provider" or "Location" or "ContractLine"))
            return (null, null, RowError.BadFormat("scope_type", "scope (Provider, Location, ContractLine)"));
        if (effectiveTo is { } to && to <= effectiveFrom!.Value)
            return (null, null, RowError.Rule("BAD_WINDOW",
                "effective_to is exclusive and must be after effective_from.",
                "تاريخ الانتهاء غير شامل ويجب أن يكون بعد تاريخ البدء."));

        var preview = RowPreview.Of(
            $"Assign {scopeType} {scopeRef} to tier {tierCode} from {effectiveFrom:yyyy-MM-dd}",
            $"إسناد {scopeType} {scopeRef} إلى الشريحة {tierCode} اعتبارًا من {effectiveFrom:yyyy-MM-dd}",
            ("tierCode", tierCode), ("scopeType", scopeType), ("scopeRef", scopeRef));

        return ((tierCode, scopeType, scopeRef!.Value, effectiveFrom!.Value, effectiveTo), preview, null);
    }
}

// ============================================================================================================
// BenefitRuleImport — DRAFT versions only
// ============================================================================================================

public sealed class BenefitRuleImportApplier(PolicyDbContext db, TimeProvider clock) : IBulkRowApplier
{
    public BulkJobType JobType => BulkJobType.BenefitRuleImport;
    public bool IsReversible => true;

    public async Task<RowOutcome> ValidateAsync(ParsedRow row, BulkScope scope, CancellationToken ct = default)
    {
        var (resolved, preview, error) = await ResolveAsync(row, ct);
        return error is not null ? new RowOutcome.Invalid(error) : new RowOutcome.Valid(resolved!, preview!);
    }

    public async Task<RowOutcome> ApplyAsync(
        ParsedRow row, BulkScope scope, Guid jobId, int rowNumber, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var (resolved, preview, error) = await ResolveAsync(row, ct);
        if (error is not null) return new RowOutcome.Invalid(error);

        var r = resolved!.Value;
        var now = clock.GetUtcNow();
        var existing = await db.BenefitRules.FirstOrDefaultAsync(
            x => x.PlanVersionId == r.PlanVersionId && x.BenefitCategoryId == r.BenefitCategoryId, ct);

        object? before = null;
        if (existing is null)
        {
            existing = new BenefitRule
            {
                RuleId = Guid.NewGuid(), PlanVersionId = r.PlanVersionId, BenefitCategoryId = r.BenefitCategoryId,
                CreatedAt = now, CreatedBy = scope.Actor.UserId,
            };
            db.BenefitRules.Add(existing);
        }
        else
        {
            before = new
            {
                isCovered = existing.IsCovered, limitType = existing.LimitType?.ToString(),
                limitValue = existing.LimitValue, resetPeriod = existing.ResetPeriod.ToString(),
                waitingPeriodDays = existing.WaitingPeriodDays, requiresPreauth = existing.RequiresPreauth,
            };
        }

        existing.IsCovered = r.IsCovered;
        existing.LimitType = r.LimitType;
        existing.LimitValue = r.LimitValue;
        existing.ResetPeriod = r.ResetPeriod;
        existing.WaitingPeriodDays = r.WaitingPeriodDays;
        existing.RequiresPreauth = r.RequiresPreauth;
        existing.Exclusions = r.Exclusions;
        existing.UpdatedAt = now;
        existing.UpdatedBy = scope.Actor.UserId;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg)
        {
            // The immutability triggers on plan_version / benefit_rule (0005) are the real enforcement. If a
            // version was activated between validation and commit, this is where it surfaces — and it must
            // surface as a row failure, not as a crash that abandons the rest of the file.
            return new RowOutcome.Invalid(RowError.Rule("PLAN_VERSION_IMMUTABLE", pg.MessageText, pg.MessageText));
        }

        return new RowOutcome.Applied(existing.RuleId, before, preview!);
    }

    public async Task<RowOutcome> ReverseAsync(BulkJobRow row, BulkScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.TargetRef is not { } ruleId) return new RowOutcome.Skipped("nothing was applied");

        var rule = await db.BenefitRules.FirstOrDefaultAsync(x => x.RuleId == ruleId, ct);
        if (rule is null) return new RowOutcome.Skipped("the rule no longer exists");

        var version = await db.PlanVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PlanVersionId == rule.PlanVersionId, ct);
        if (version is not null && version.Status != PlanVersionStatus.Draft)
            return new RowOutcome.Invalid(RowError.Rule("PLAN_VERSION_ACTIVATED",
                "This plan version has been activated since the import, and an activated version is immutable. " +
                "Amend it into a new version instead.",
                "تم تفعيل نسخة الخطة بعد الاستيراد، والنسخة المفعّلة غير قابلة للتعديل. " +
                "يُرجى إنشاء نسخة جديدة بدلًا من ذلك."));

        var before = BulkSnapshots.Read(row.BeforeSnapshot);
        if (before is null)
        {
            // The row CREATED the rule; reversing it removes it. Safe only because the version is still a
            // draft — a draft has never been in force, so no entitlement depends on this row.
            db.BenefitRules.Remove(rule);
            await db.SaveChangesAsync(ct);
            return new RowOutcome.Applied(ruleId, null, RowPreview.Of(
                "Imported benefit rule removed", "تمت إزالة قاعدة المنفعة المستوردة", ("ruleId", ruleId)));
        }

        rule.IsCovered = before.TryGetValue("isCovered", out var c) && bool.TryParse(c?.ToString(), out var cov) && cov;
        rule.LimitValue = before.TryGetValue("limitValue", out var lv) && decimal.TryParse(
            lv?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var limit) ? limit : null;
        rule.WaitingPeriodDays = before.TryGetValue("waitingPeriodDays", out var wp) && int.TryParse(
            wp?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ? days : 0;
        rule.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return new RowOutcome.Applied(ruleId, null, RowPreview.Of(
            "Benefit rule restored", "تمت استعادة قاعدة المنفعة", ("ruleId", ruleId)));
    }

    private async Task<((Guid PlanVersionId, Guid BenefitCategoryId, bool IsCovered, LimitType? LimitType,
        decimal? LimitValue, ResetPeriod ResetPeriod, int WaitingPeriodDays, bool RequiresPreauth, string Exclusions)? Resolved,
        RowPreview? Preview, RowError? Error)> ResolveAsync(ParsedRow row, CancellationToken ct)
    {
        if (BulkResolve.Guid(row, "plan_version_id", true, out var planVersionId) is { } e1) return (null, null, e1);
        if (BulkResolve.Required(row, "benefit_category_code", out var categoryCode) is { } e2) return (null, null, e2);
        if (BulkResolve.Required(row, "is_covered", out var rawCovered) is { } e3) return (null, null, e3);
        if (!BulkCells.TryBool(rawCovered, out var isCovered)) return (null, null, RowError.BadFormat("is_covered", "yes/no"));

        var version = await db.PlanVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.PlanVersionId == planVersionId!.Value, ct);
        if (version is null) return (null, null, RowError.Unknown("Plan version", planVersionId!.Value.ToString()));

        // The rule the build prompt states outright: a DRAFT version only. An Active configuration is what
        // thousands of memberships were generated from; a bulk path into it would change what people are
        // entitled to without the amend → new version → activate review that exists for exactly that reason.
        if (version.Status != PlanVersionStatus.Draft)
            return (null, null, RowError.Rule("PLAN_VERSION_NOT_DRAFT",
                $"Plan version {version.VersionNo} is {version.Status}. Benefit rules can only be imported into a " +
                "Draft version — amend the plan into a new version first.",
                $"نسخة الخطة {version.VersionNo} بحالة {version.Status}. لا يمكن استيراد قواعد المنافع إلا إلى نسخة مسودة."));

        var category = await db.BenefitCategories.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code == categoryCode, ct);
        if (category is null) return (null, null, RowError.Unknown("Benefit category", categoryCode));

        LimitType? limitType = null;
        if (row.Text("limit_type") is { } rawLimitType)
        {
            if (!Enum.TryParse<LimitType>(rawLimitType, ignoreCase: true, out var parsed))
                return (null, null, RowError.BadFormat("limit_type", "limit type (Annual, PerEncounter, Lifetime, Count)"));
            limitType = parsed;
        }

        decimal? limitValue = null;
        if (row.Text("limit_value") is { } rawLimit)
        {
            if (!BulkCells.TryDecimal(rawLimit, out var parsed)) return (null, null, RowError.BadFormat("limit_value", "number"));
            if (parsed < 0m) return (null, null, RowError.Rule("NEGATIVE_LIMIT",
                "A limit cannot be negative.", "لا يمكن أن يكون الحد الأقصى بالسالب."));
            limitValue = parsed;
        }

        var resetPeriod = ResetPeriod.None;
        if (row.Text("reset_period") is { } rawReset
            && !Enum.TryParse(rawReset, ignoreCase: true, out resetPeriod))
            return (null, null, RowError.BadFormat("reset_period", "reset period (None, Monthly, Quarterly, Yearly)"));

        var waitingDays = 0;
        if (row.Text("waiting_period_days") is { } rawWait)
        {
            if (!BulkCells.TryInt(rawWait, out waitingDays)) return (null, null, RowError.BadFormat("waiting_period_days", "whole number"));
            if (waitingDays < 0) return (null, null, RowError.Rule("NEGATIVE_WAITING_PERIOD",
                "A waiting period cannot be negative.", "لا يمكن أن تكون فترة الانتظار بالسالب."));
        }

        var requiresPreauth = false;
        if (row.Text("requires_preauth") is { } rawPreauth && !BulkCells.TryBool(rawPreauth, out requiresPreauth))
            return (null, null, RowError.BadFormat("requires_preauth", "yes/no"));

        var exclusions = BulkSnapshots.ExclusionsJson(row.Text("exclusions"));

        var preview = RowPreview.Of(
            $"Set {categoryCode} on draft version {version.VersionNo}: {(isCovered ? "covered" : "not covered")}",
            $"ضبط {categoryCode} في المسودة {version.VersionNo}: {(isCovered ? "مغطاة" : "غير مغطاة")}",
            ("benefitCategory", categoryCode), ("isCovered", isCovered), ("limitValue", limitValue));

        return ((planVersionId!.Value, category.BenefitCategoryId, isCovered, limitType, limitValue,
            resetPeriod, waitingDays, requiresPreauth, exclusions), preview, null);
    }
}
