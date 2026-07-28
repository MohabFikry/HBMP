using Mersal.BenefitPricing;
using System.Globalization;
using System.Text;
using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;

namespace Mersal.Policy.Api;

/// <summary>
/// Phase 19.5 — policy query and member query (design 38 §4.4).
///
/// <para>These are STRUCTURED SEARCH, not single-identifier lookup, and that difference is what makes them a
/// disclosure surface rather than a convenience. "Every member of this policy over 80% of their limit" returns
/// a list of people, and a list is the highest-volume disclosure the platform makes — so the page size is
/// capped, the payer restriction is a predicate inside the SQL (including the row COUNT), the sort field comes
/// from an allow-list, and every call is audited with the filter that produced it.</para>
/// </summary>
public static class QueryEndpoints
{
    public static void MapAdministrativeQueries(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/v1").RequireAuthorization(HbmpPolicies.Scope("policy:read"));

        MapPolicyQuery(read);
        MapMemberQuery(read);
    }

    // ---- Policy query ------------------------------------------------------------------------------------

    private static void MapPolicyQuery(RouteGroupBuilder read)
    {
        read.MapGet("/policy-query", async (
            Guid? payerId, Guid? planId, string? planLabel, string? status, string? policyNo,
            DateOnly? effectiveOn, DateOnly? effectiveFromAfter, DateOnly? effectiveToBefore,
            Guid? groupId, string? memberCountBand, string? utilizationBand,
            int? page, int? pageSize, string? sort, string? format,
            AdministrativeQuery query, PolicyGate gate, IPayerDirectory payers,
            IAuditClient audit, CancellationToken ct) =>
        {
            var principal = gate.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            if (!TryParseFilter(status, effectiveOn, memberCountBand, utilizationBand, out var parsed, out var error))
                return error!;

            if (!SortRequest.TryParse(sort, PolicySortFields.Allowed, PolicySortFields.Default, out var sortReq))
                return ProblemResults.Invalid("UNKNOWN_SORT_FIELD",
                    $"'{sort}' is not sortable. Allowed: {string.Join(", ", PolicySortFields.Allowed.Order(StringComparer.Ordinal))}.");

            var permitted = await payers.GetAsync(principal, ct);

            // A caller who NAMES a payer they may not see gets 403, not an empty page. See PayerScopeRules:
            // answering "no such policy" to an administrator looking straight at the policy number sends them
            // to raise a data-loss incident.
            if (payerId is { } requestedPayer && !permitted.Allows(requestedPayer))
            {
                await AuditScopeDenial(audit, principal, "payer", requestedPayer.ToString(), ct);
                return GateResults.Forbidden("urn:hbmp:payer-scope-denied",
                    detail: "You are not permitted to read this payer's policies.", reason: "payer-not-permitted");
            }

            var filter = new PolicyQueryFilter(
                payerId, planId, planLabel, parsed.Status, effectiveOn, effectiveFromAfter, effectiveToBefore,
                groupId, parsed.CountBand, parsed.Band, policyNo);

            var pageReq = PageRequest.Of(page, pageSize);
            var result = await query.PolicyQueryAsync(filter, pageReq, sortReq, permitted, ct);

            var mayAmounts = AdministrativeProjection.MayReadAmounts(principal.Roles);
            var mayContract = AdministrativeProjection.MayReadContract(principal.Roles);
            var rows = result.Items.Select(r => PolicyQueryRowView.From(r, mayAmounts, mayContract)).ToList();

            var descriptor = Describe(filter);
            if (IsCsv(format))
            {
                await AuditQuery(audit, principal, "policy", AuditAction.Export, descriptor, result.TotalCount, rows.Count, permitted, ct);
                return Csv.PolicyRows(rows, descriptor);
            }

            await AuditQuery(audit, principal, "policy", AuditAction.Read, descriptor, result.TotalCount, rows.Count, permitted, ct);

            return Results.Ok(new QueryPageView<PolicyQueryRowView>(
                rows, result.Page, result.PageSize, result.TotalCount, result.TotalPages,
                (sortReq.Descending ? "-" : "") + sortReq.Field,
                PayerScopeApplied: !permitted.IsUnrestricted,
                IdentityMatchTruncated: false,
                Unavailable: []));
        });
    }

    // ---- Member query ------------------------------------------------------------------------------------

    private static void MapMemberQuery(RouteGroupBuilder read)
    {
        read.MapGet("/member-query", async (
            string? identifierType, string? identifierValue, string? name, string? memberNo,
            Guid? policyId, Guid? policyPlanId, Guid? groupId, string? relationship, string? status,
            Guid? branchId, DateOnly? enrolledOn, DateOnly? enrolledFromAfter, DateOnly? enrolledToBefore,
            string? waitingPeriod, string? utilizationBand,
            int? page, int? pageSize, string? sort, string? format,
            AdministrativeQuery query, PolicyGate gate, IPayerDirectory payers, IBranchDirectory branches,
            IBeneficiaryAdministrativeSource patient, IAuditClient audit, IBusinessCalendar calendar,
            HttpContext http, CancellationToken ct) =>
        {
            var principal = gate.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            var asOf = calendar.Today();   // 18.A3 — waiting-period state is a Cairo day
            var token = http.Request.Headers.Authorization.FirstOrDefault();

            if (!TryParseMemberFacets(relationship, status, waitingPeriod, utilizationBand, out var facets, out var facetError))
                return facetError!;

            if (!SortRequest.TryParse(sort, MemberSortFields.Allowed, MemberSortFields.Default, out var sortReq))
                return ProblemResults.Invalid("UNKNOWN_SORT_FIELD",
                    $"'{sort}' is not sortable. Allowed: {string.Join(", ", MemberSortFields.Allowed.Order(StringComparer.Ordinal))}.");

            var permitted = await payers.GetAsync(principal, ct);

            if (policyId is { } requestedPolicy)
            {
                var (exists, policyPayer) = await query.PolicyPayerAsync(requestedPolicy, ct);
                if (!exists) return ProblemResults.NotFound("POLICY_NOT_FOUND", "No such policy.");
                if (PayerScopeRules.Check(permitted, policyPayer) == PayerScopeOutcome.Denied)
                {
                    await AuditScopeDenial(audit, principal, "policy", requestedPolicy.ToString(), ct);
                    return GateResults.Forbidden("urn:hbmp:payer-scope-denied",
                        detail: "You are not permitted to read this policy's members.", reason: "payer-not-permitted");
                }
            }

            // Branch NARROWING, resolved on demand rather than in middleware: design 38 §6 makes policy
            // administration member-scoped (all branches), so narrowing every route in this service would
            // enforce a boundary the surface does not have. It is applied here because a member LIST is the one
            // place an operational role can sweep beyond their branch.
            var effectiveBranch = branchId;
            var unavailable = new List<string>();
            if (BranchScopeModes.ModeFor(principal) == ScopeMode.BranchScoped)
            {
                var state = await BranchScopeResolver.ResolveAsync(
                    principal, http.Request.Headers[BranchHeaders.ActiveBranch].FirstOrDefault(), branches, ct);
                if (state.Denied)
                    return GateResults.Forbidden("urn:hbmp:branch-scope-denied",
                        detail: "The requested active branch is not in your permitted set.", reason: "branch-not-permitted");
                // An explicit filter inside the permitted set is honoured; anything else falls back to the
                // active branch. A branch-scoped caller cannot widen their own query by naming a branch.
                if (branchId is { } asked && !state.Context.PermittedBranchIds.Contains(asked))
                    return GateResults.Forbidden("urn:hbmp:branch-scope-denied",
                        detail: "That branch is not in your permitted set.", reason: "branch-not-permitted");
                effectiveBranch ??= state.Context.ActiveBranchId;
            }

            // Identity filters are resolved at the OWNER. A null result means patient-service could not be
            // asked — which must NOT silently become "no identity filter", or a failed lookup would answer with
            // the whole membership.
            IReadOnlyList<Guid>? beneficiaryIds = null;
            var truncated = false;
            if (!string.IsNullOrWhiteSpace(identifierValue) || !string.IsNullOrWhiteSpace(name))
            {
                var search = await patient.SearchAsync(identifierType, identifierValue, name, token, ct);
                if (search is null)
                    return Results.Problem(statusCode: 503, title: "identity lookup unavailable",
                        detail: "The beneficiary directory could not be reached, so a name or identifier filter cannot be applied. Retry, or search by member number.");
                beneficiaryIds = search.Value.Ids;
                truncated = search.Value.Truncated;
            }

            var filter = new MemberQueryFilter(
                policyId, policyPlanId, groupId, facets.Relationship, facets.Status, effectiveBranch,
                enrolledOn, enrolledFromAfter, enrolledToBefore, facets.WaitingPeriod, facets.Band,
                memberNo, beneficiaryIds);

            var pageReq = PageRequest.Of(page, pageSize);
            var result = await query.MemberQueryAsync(filter, pageReq, sortReq, permitted, asOf, ct);

            // Names for THIS PAGE only, batched at the owner. A 40 000-row filter never becomes a 40 000-name
            // disclosure, because only the rows someone is actually looking at are ever resolved.
            IReadOnlyDictionary<Guid, BeneficiarySummary> summaries = new Dictionary<Guid, BeneficiarySummary>();
            if (result.Items.Count > 0)
            {
                var resolved = await patient.SummariesAsync(
                    [.. result.Items.Select(r => r.BeneficiaryId).Distinct()], token, ct);
                if (resolved is null) unavailable.Add("patient-service");
                else summaries = resolved;
            }

            var mayAmounts = AdministrativeProjection.MayReadAmounts(principal.Roles);
            var mayContract = AdministrativeProjection.MayReadContract(principal.Roles);
            var mayCase = AdministrativeProjection.MayReadCase(principal.Roles);
            var rows = result.Items
                .Select(r => MemberQueryRowView.From(
                    r, summaries.GetValueOrDefault(r.BeneficiaryId), asOf, mayAmounts, mayContract, mayCase))
                .ToList();

            var descriptor = Describe(filter, identifierType, name);
            if (IsCsv(format))
            {
                await AuditQuery(audit, principal, "enrollment", AuditAction.Export, descriptor, result.TotalCount, rows.Count, permitted, ct);
                return Csv.MemberRows(rows, descriptor);
            }

            await AuditQuery(audit, principal, "enrollment", AuditAction.Read, descriptor, result.TotalCount, rows.Count, permitted, ct);

            return Results.Ok(new QueryPageView<MemberQueryRowView>(
                rows, result.Page, result.PageSize, result.TotalCount, result.TotalPages,
                (sortReq.Descending ? "-" : "") + sortReq.Field,
                PayerScopeApplied: !permitted.IsUnrestricted,
                IdentityMatchTruncated: truncated,
                Unavailable: unavailable));
        });
    }

    // ---- Parsing -----------------------------------------------------------------------------------------

    private static bool TryParseFilter(
        string? status, DateOnly? effectiveOn, string? memberCountBand, string? utilizationBand,
        out (PolicyStatus? Status, MemberCountBand? CountBand, UtilizationBand? Band) parsed, out IResult? error)
    {
        parsed = default;
        error = null;

        PolicyStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<PolicyStatus>(status, ignoreCase: true, out var s))
            { error = ProblemResults.Invalid("UNKNOWN_STATUS", $"'{status}' is not a policy status."); return false; }
            parsedStatus = s;
        }

        MemberCountBand? countBand = null;
        if (!string.IsNullOrWhiteSpace(memberCountBand))
        {
            if (!MemberCountBands.TryParse(memberCountBand, out var cb))
            { error = ProblemResults.Invalid("UNKNOWN_BAND", $"'{memberCountBand}' is not a member-count band."); return false; }
            countBand = cb;
        }

        UtilizationBand? band = null;
        if (!string.IsNullOrWhiteSpace(utilizationBand))
        {
            if (!UtilizationBands.TryParse(utilizationBand, out var ub))
            { error = ProblemResults.Invalid("UNKNOWN_BAND", $"'{utilizationBand}' is not a utilization band."); return false; }
            band = ub;
        }

        _ = effectiveOn;
        parsed = (parsedStatus, countBand, band);
        return true;
    }

    private static bool TryParseMemberFacets(
        string? relationship, string? status, string? waitingPeriod, string? utilizationBand,
        out (Relationship? Relationship, EnrollmentStatus? Status, WaitingPeriodState? WaitingPeriod, UtilizationBand? Band) facets,
        out IResult? error)
    {
        facets = default;
        error = null;

        Relationship? rel = null;
        if (!string.IsNullOrWhiteSpace(relationship))
        {
            if (!Enum.TryParse<Relationship>(relationship, ignoreCase: true, out var r))
            { error = ProblemResults.Invalid("UNKNOWN_RELATIONSHIP", $"'{relationship}' is not a relationship."); return false; }
            rel = r;
        }

        EnrollmentStatus? st = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<EnrollmentStatus>(status, ignoreCase: true, out var s))
            { error = ProblemResults.Invalid("UNKNOWN_STATUS", $"'{status}' is not an enrolment status."); return false; }
            st = s;
        }

        WaitingPeriodState? wp = null;
        if (!string.IsNullOrWhiteSpace(waitingPeriod))
        {
            if (!Enum.TryParse<WaitingPeriodState>(waitingPeriod, ignoreCase: true, out var w))
            { error = ProblemResults.Invalid("UNKNOWN_WAITING_STATE", $"'{waitingPeriod}' is not a waiting-period state."); return false; }
            wp = w;
        }

        UtilizationBand? band = null;
        if (!string.IsNullOrWhiteSpace(utilizationBand))
        {
            if (!UtilizationBands.TryParse(utilizationBand, out var ub))
            { error = ProblemResults.Invalid("UNKNOWN_BAND", $"'{utilizationBand}' is not a utilization band."); return false; }
            band = ub;
        }

        facets = (rel, st, wp, band);
        return true;
    }

    private static bool IsCsv(string? format) =>
        string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);

    // ---- Audit -------------------------------------------------------------------------------------------

    /// <summary>
    /// A query is audited with the FILTER, the total and the disclosed count.
    ///
    /// <para>All three matter for different reasons. The filter is what a later review needs to know what was
    /// asked; the total says how much the caller learned EXISTS even though they saw one page; the disclosed
    /// count says how many rows actually left. "Somebody ran a member query" is not an audit trail.</para>
    /// </summary>
    private static async Task AuditQuery(
        IAuditClient audit, HbmpPrincipal principal, string entity, AuditAction action,
        string descriptor, int total, int disclosed, PermittedPayers permitted, CancellationToken ct) =>
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = entity, EntityId = action == AuditAction.Export ? "query:export" : "query",
            Action = action,
            ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
            TenantId = principal.TenantId,
            DecisionOutcome = $"matched={total};disclosed={disclosed}",
            DecisionReasonCode = permitted.IsUnrestricted ? descriptor : $"payer-scoped;{descriptor}",
            FieldClasses = ["coverage"],
            Severity = action == AuditAction.Export ? AuditSeverity.Notice : AuditSeverity.Info,
        }, ct);

    private static async Task AuditScopeDenial(
        IAuditClient audit, HbmpPrincipal principal, string kind, string id, CancellationToken ct) =>
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = kind, EntityId = id, Action = AuditAction.Decision,
            ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
            TenantId = principal.TenantId,
            DecisionOutcome = "PayerScopeDenied", DecisionReasonCode = "payer-not-permitted",
            Severity = AuditSeverity.High,
        }, ct);

    private static string Describe(PolicyQueryFilter f)
    {
        var parts = new List<string>();
        if (f.PayerId is { } p) parts.Add($"payer:{p}");
        if (f.PlanId is { } pl) parts.Add($"plan:{pl}");
        if (!string.IsNullOrWhiteSpace(f.PlanLabel)) parts.Add($"label:{f.PlanLabel}");
        if (f.Status is { } s) parts.Add($"status:{s}");
        if (f.GroupId is { } g) parts.Add($"group:{g}");
        if (f.MemberCountBand is { } cb) parts.Add($"members:{cb}");
        if (f.UtilizationBand is { } ub) parts.Add($"utilization:{ub}");
        if (f.EffectiveOn is { } on) parts.Add($"on:{on:yyyy-MM-dd}");
        return parts.Count == 0 ? "unfiltered" : string.Join(';', parts);
    }

    private static string Describe(MemberQueryFilter f, string? identifierType, string? name)
    {
        var parts = new List<string>();
        if (f.PolicyId is { } p) parts.Add($"policy:{p}");
        if (f.PolicyPlanId is { } pp) parts.Add($"plan:{pp}");
        if (f.GroupId is { } g) parts.Add($"group:{g}");
        if (f.Relationship is { } r) parts.Add($"relationship:{r}");
        if (f.Status is { } s) parts.Add($"status:{s}");
        if (f.BranchId is { } b) parts.Add($"branch:{b}");
        if (f.WaitingPeriod is { } w) parts.Add($"waiting:{w}");
        if (f.UtilizationBand is { } ub) parts.Add($"utilization:{ub}");
        // The identifier TYPE is recorded; the VALUE never is. An audit log that stores the UNHCR number
        // somebody searched for has turned the compliance record into a second copy of the SPI it protects.
        if (!string.IsNullOrWhiteSpace(identifierType)) parts.Add($"identifierType:{identifierType}");
        if (!string.IsNullOrWhiteSpace(name)) parts.Add("nameSearch");
        if (!string.IsNullOrWhiteSpace(f.MemberNo)) parts.Add("memberNo");
        return parts.Count == 0 ? "unfiltered" : string.Join(';', parts);
    }

    // ---- CSV ---------------------------------------------------------------------------------------------

    /// <summary>Column allow-lists, written out rather than reflected over the view types — so adding a
    /// property to a DTO cannot silently add a column to everyone's spreadsheet. A null (projected-away) value
    /// exports as EMPTY, never as a zero: a finance-free caller's export must not read as "limit 0".</summary>
    private static class Csv
    {
        public static IResult PolicyRows(IReadOnlyList<PolicyQueryRowView> rows, string descriptor)
        {
            var csv = new StringBuilder();
            csv.AppendLine("policyNo,status,effectiveFrom,effectiveTo,memberCount,memberCountBand,planCount,totalLimit,totalConsumed,percentUsed,utilizationBand");
            foreach (var r in rows)
            {
                csv.Append(Escape(r.PolicyNo)).Append(',')
                   .Append(r.Status).Append(',')
                   .Append(Iso(r.EffectiveFrom)).Append(',')
                   .Append(r.EffectiveTo is { } t ? Iso(t) : "").Append(',')
                   .Append(r.MemberCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(r.MemberCountBand).Append(',')
                   .Append(r.PlanCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(Num(r.TotalLimit)).Append(',')
                   .Append(Num(r.TotalConsumed)).Append(',')
                   .Append(Num(r.PercentUsed)).Append(',')
                   .Append(r.UtilizationBand).AppendLine();
            }
            return File(csv, $"policy-query-{descriptor}");
        }

        public static IResult MemberRows(IReadOnlyList<MemberQueryRowView> rows, string descriptor)
        {
            var csv = new StringBuilder();
            csv.AppendLine("memberNo,givenName,familyName,relationship,status,planLabel,effectiveFrom,effectiveTo,waitingPeriodState,totalLimit,totalConsumed,totalRemaining,percentUsed,utilizationBand");
            foreach (var r in rows)
            {
                csv.Append(Escape(r.MemberNo)).Append(',')
                   .Append(Escape(r.GivenName)).Append(',')
                   .Append(Escape(r.FamilyName)).Append(',')
                   .Append(r.Relationship).Append(',')
                   .Append(r.Status).Append(',')
                   .Append(Escape(r.PlanLabel)).Append(',')
                   .Append(Iso(r.EffectiveFrom)).Append(',')
                   .Append(r.EffectiveTo is { } t ? Iso(t) : "").Append(',')
                   .Append(r.WaitingPeriodState).Append(',')
                   .Append(Num(r.TotalLimit)).Append(',')
                   .Append(Num(r.TotalConsumed)).Append(',')
                   .Append(Num(r.TotalRemaining)).Append(',')
                   .Append(Num(r.PercentUsed)).Append(',')
                   .Append(r.UtilizationBand).AppendLine();
            }
            return File(csv, $"member-query-{descriptor}");
        }

        private static IResult File(StringBuilder csv, string name) =>
            Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv",
                $"{Sanitize(name)}.csv");

        private static string Sanitize(string name) =>
            new([.. name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);

        private static string Num(decimal? d) =>
            d is null ? "" : d.Value.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>CSV quoting, plus a leading apostrophe on anything a spreadsheet would evaluate. A member
        /// number is not a formula, and an export opened in Excel should not be able to run one.</summary>
        private static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var v = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
            return v.Contains(',', StringComparison.Ordinal) || v.Contains('"', StringComparison.Ordinal)
                ? $"\"{v.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
                : v;
        }
    }
}
