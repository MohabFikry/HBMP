using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Api;

/// <summary>
/// Phase 19.5 — full coverage details (design 38 §4.5) and the administrative 360 (§4.6).
///
/// <para>These two are the "what am I actually entitled to" pages. Coverage details answers it from the
/// configuration and the accumulator together; the 360 answers "who is this person, administratively" by
/// COMPOSING the owners rather than copying them.</para>
/// </summary>
public static class CoverageDetailEndpoints
{
    public static void MapCoverageDetails(this IEndpointRouteBuilder app)
    {
        // PROFILE SECTION SOURCES. Both of these fill sections of the unified patient profile, so they are
        // reachable with policy:read OR profile:read. Design 39 forbids service accounts — profile-service
        // forwards the CALLER's bearer (NoServiceAccountArchitectureTests asserts it on the wire) — which means
        // every role whose profile projection includes the header or coverage section must be able to open the
        // endpoint that fills it. The call centre holds profile:read and not policy:read, so requiring
        // policy:read alone composed header, alerts and coverage as Unavailable/upstream-error and made the
        // workspace report a live member as 404 Not Found. Min-necessary is untouched: ProfilePolicies still
        // decides, per role, which sections come back and with which field variant.
        var sections = app.MapGroup("/api/v1")
            .RequireAuthorization(HbmpPolicies.AnyScope("policy:read", "profile:read"));

        MapCoverageDetail(sections);
        MapAdministrative360(sections);
        MapCoveredFamily(sections);
    }

    // ---- Coverage details --------------------------------------------------------------------------------

    private static void MapCoverageDetail(RouteGroupBuilder read)
    {
        read.MapGet("/enrollments/{enrollmentId:guid}/coverage-details", async (
            Guid enrollmentId, DateOnly? asOf, DateOnly? serviceDate,
            PolicyDbContext db, AdministrativeQuery query, IPlanVersionResolver versions,
            PolicyGate gate, IPayerDirectory payers, IAuditClient audit, IBusinessCalendar calendar,
            CancellationToken ct) =>
        {
            var principal = gate.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            // THE DATE THAT DECIDES WHICH RULES APPLY. Defaults to today, but a caller adjudicating February's
            // claim passes February's service date and must be shown February's version — design 38 §7.1,
            // and the reason this endpoint resolves rather than reading "the current version".
            var on = serviceDate ?? asOf ?? calendar.Today();

            var (exists, payerId) = await query.EnrollmentPayerAsync(enrollmentId, ct);
            if (!exists) return ProblemResults.NotFound("ENROLLMENT_NOT_FOUND", "No such enrolment.");

            var permitted = await payers.GetAsync(principal, ct);
            if (PayerScopeRules.Check(permitted, payerId) == PayerScopeOutcome.Denied)
                return GateResults.Forbidden("urn:hbmp:payer-scope-denied",
                    detail: "You are not permitted to read this member's coverage.", reason: "payer-not-permitted");

            var enrollment = await db.Enrollments.AsNoTracking()
                .FirstOrDefaultAsync(e => e.EnrollmentId == enrollmentId && !e.IsDeleted, ct);
            if (enrollment is null) return ProblemResults.NotFound("ENROLLMENT_NOT_FOUND", "No such enrolment.");

            var policyPlan = await db.PolicyPlans.AsNoTracking()
                .FirstOrDefaultAsync(pp => pp.PolicyPlanId == enrollment.PolicyPlanId, ct);

            // The plan_plan points at ONE version; the plan behind it is what gets resolved for the date. That
            // indirection is what makes "v1's rules for a February service date, even though v2 is Active now"
            // work without storing a version per date on every membership.
            var pinned = policyPlan is null ? null : await db.PlanVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.PlanVersionId == policyPlan.PlanVersionId, ct);
            var inForce = pinned is null ? null : await versions.ResolveAsync(pinned.PlanId, on, ct);

            var rulesByCategory = new Dictionary<Guid, BenefitRule>();
            if (inForce is not null)
            {
                var ruleIds = inForce.Rules.Select(r => r.RuleId).ToList();
                var tiers = await db.BenefitRuleTiers.AsNoTracking()
                    .Where(t => ruleIds.Contains(t.BenefitRuleId)).ToListAsync(ct);
                foreach (var rule in inForce.Rules)
                {
                    rule.Tiers = [.. tiers.Where(t => t.BenefitRuleId == rule.RuleId)];
                    rulesByCategory[rule.BenefitCategoryId] = rule;
                }
            }

            var coverages = await db.Coverages.AsNoTracking().Include(c => c.Limits)
                .Where(c => c.BeneficiaryId == enrollment.BeneficiaryId && !c.IsDeleted
                            && c.Status == CoverageStatus.Active)
                .ToListAsync(ct);

            var categories = await db.BenefitCategories.AsNoTracking().ToListAsync(ct);
            var categoryById = categories.ToDictionary(c => c.BenefitCategoryId);

            // Every category the member HOLDS, plus every category the version in force CONFIGURES. The union,
            // because a category configured but not held ("you would be covered, but your enrolment predates
            // it") and one held but no longer configured ("your balance is still spendable") are both real, and
            // both invisible if either side alone drives the list.
            var categoryIds = coverages.Select(c => c.BenefitCategoryId)
                .Union(rulesByCategory.Keys)
                .Where(categoryById.ContainsKey)
                .Distinct()
                .OrderBy(id => categoryById[id].Code, StringComparer.Ordinal)
                .ToList();

            var details = categoryIds.ConvertAll(id => CoverageDetailAssembler.Category(
                categoryById[id].Code,
                rulesByCategory.GetValueOrDefault(id),
                coverages.FirstOrDefault(c => c.BenefitCategoryId == id),
                enrollment.EffectiveFrom,
                on));

            var history = await db.EnrollmentEvents.AsNoTracking()
                .Where(e => e.EnrollmentId == enrollmentId)
                .OrderByDescending(e => e.OccurredAt)
                .Take(200)
                .ToListAsync(ct);

            var view = new MemberCoverageDetail(
                enrollment.EnrollmentId, enrollment.BeneficiaryId, enrollment.MemberNo, enrollment.PolicyId,
                enrollment.PolicyPlanId, policyPlan?.PlanLabel ?? "", pinned?.PlanId,
                inForce?.PlanVersionId, inForce?.VersionNo, inForce?.EffectiveFrom, inForce?.EffectiveTo,
                inForce?.Status.ToString(),
                enrollment.SourcePlanVersionId,
                PlanVersionChangedSinceEnrolment:
                    enrollment.SourcePlanVersionId is { } src && inForce is not null && src != inForce.PlanVersionId,
                on, enrollment.Status.ToString(), enrollment.EffectiveFrom, enrollment.EffectiveTo,
                details,
                [.. history.Select(h => new CoverageChangeEntry(
                    h.EventId, h.EventType.ToString(), h.EffectiveDate, h.OccurredAt, h.IsRetroEffective,
                    h.Reason, h.Payload))]);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "enrollment", EntityId = enrollmentId.ToString(), Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                DecisionOutcome = "coverage-details",
                DecisionReasonCode = $"asOf:{on:yyyy-MM-dd};version:{inForce?.VersionNo.ToString() ?? "none"}",
                FieldClasses = ["coverage"],
            }, ct);

            return Results.Ok(view);
        });
    }

    // ---- Administrative 360 ------------------------------------------------------------------------------

    private static void MapAdministrative360(RouteGroupBuilder read)
    {
        read.MapGet("/beneficiaries/{beneficiaryId:guid}/administrative-360", async (
            Guid beneficiaryId,
            PolicyDbContext db, AdministrativeQuery query, IBeneficiaryAdministrativeSource patient,
            PolicyGate gate, IPayerDirectory payers, IAuditClient audit, IBusinessCalendar calendar,
            HttpContext http, CancellationToken ct) =>
        {
            var principal = gate.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            var asOf = calendar.Today();
            var token = http.Request.Headers.Authorization.FirstOrDefault();
            var permitted = await payers.GetAsync(principal, ct);

            var enrollments = await db.Enrollments.AsNoTracking()
                .Where(e => e.BeneficiaryId == beneficiaryId && !e.IsDeleted)
                .OrderByDescending(e => e.EffectiveFrom)
                .ToListAsync(ct);

            var policyIds = enrollments.Select(e => e.PolicyId).Distinct().ToList();
            var policies = await db.Policies.AsNoTracking()
                .Where(p => policyIds.Contains(p.PolicyId))
                .Select(p => new { p.PolicyId, p.PolicyNo, p.PayerId })
                .ToListAsync(ct);
            var policyById = policies.ToDictionary(p => p.PolicyId);

            // A beneficiary can be enrolled under more than one policy. Sections behind a payer the caller may
            // not see are WITHHELD AND NAMED — refusing the whole record would hide the fact that the person
            // has other cover, which is exactly what an officer needs to know to route the question onward.
            var visible = enrollments
                .Where(e => PayerScopeRules.Check(
                    permitted, policyById.GetValueOrDefault(e.PolicyId)?.PayerId) == PayerScopeOutcome.Allowed)
                .ToList();
            var withheld = new List<string>();
            if (visible.Count < enrollments.Count)
                withheld.Add($"memberships:{enrollments.Count - visible.Count}");

            if (enrollments.Count > 0 && visible.Count == 0)
                return GateResults.Forbidden("urn:hbmp:payer-scope-denied",
                    detail: "This beneficiary's memberships all belong to payers outside your scope.",
                    reason: "payer-not-permitted");

            var unavailable = new List<string>();

            // patient-service answers for ITSELF, with its own projection and its own PHI-read audit. What
            // comes back is passed through unmodelled — see AdministrativeSeams.
            var facts = await patient.GetAsync(beneficiaryId, token, ct);
            if (facts is null) unavailable.Add("patient-service");

            var visibleIds = visible.Select(e => e.EnrollmentId).ToList();

            var groupIds = visible.Where(e => e.GroupId is not null).Select(e => e.GroupId!.Value).Distinct().ToList();
            var groups = await db.MemberGroups.AsNoTracking()
                .Where(g => groupIds.Contains(g.GroupId))
                .Select(g => new { g.GroupId, g.GroupCode })
                .ToListAsync(ct);
            var groupById = groups.ToDictionary(g => g.GroupId, g => g.GroupCode);

            var planLabels = await db.PolicyPlans.AsNoTracking()
                .Where(pp => visible.Select(e => e.PolicyPlanId).Contains(pp.PolicyPlanId))
                .Select(pp => new { pp.PolicyPlanId, pp.PlanLabel })
                .ToListAsync(ct);
            var labelById = planLabels.ToDictionary(p => p.PolicyPlanId, p => p.PlanLabel);

            var memberships = visible.ConvertAll(e =>
            {
                var policy = policyById.GetValueOrDefault(e.PolicyId);
                return new MembershipSummaryView(
                    e.EnrollmentId, e.MemberNo, e.PolicyId, policy?.PolicyNo,
                    AdministrativeProjection.MayReadContract(principal.Roles) ? policy?.PayerId : null,
                    e.PolicyPlanId, labelById.GetValueOrDefault(e.PolicyPlanId), e.GroupId,
                    e.GroupId is { } g ? groupById.GetValueOrDefault(g) : null,
                    e.Relationship.ToString(), e.Status.ToString(), e.EffectiveFrom, e.EffectiveTo,
                    e.WaitingPeriodEndsOn,
                    (e.WaitingPeriodEndsOn is not { } ends ? WaitingPeriodState.None
                        : asOf <= ends ? WaitingPeriodState.Serving
                        : WaitingPeriodState.Served).ToString(),
                    e.BranchId,
                    AdministrativeProjection.MayReadCase(principal.Roles) ? e.TerminationReason : null);
            });

            // The COVERED family: who is enrolled under whom. A membership fact policy-service owns — and
            // deliberately not patient-service's household, which answers "who lives with this person" and
            // would disagree the moment a relative is not enrolled.
            var family = await CoveredFamilyAsync(db, query, patient, token, visible, ct);

            var history = await db.EnrollmentEvents.AsNoTracking()
                .Where(e => visibleIds.Contains(e.EnrollmentId))
                .OrderByDescending(e => e.OccurredAt)
                .Take(200)
                .ToListAsync(ct);

            // Documents and notes are already policy-service's own (19.3 / 19.3b) and are read here rather than
            // re-fetched from document-service: re-fetching would duplicate the classification decision that
            // 19.3b deliberately kept in one place.
            var documents = await db.PolicyDocuments.AsNoTracking()
                .Where(d => d.Scope == NoteScope.Member && visibleIds.Contains(d.ScopeRef)
                            && d.Status == DocumentLinkStatus.Active)
                .OrderByDescending(d => d.UploadedAt)
                .ToListAsync(ct);

            var notes = await db.Notes.AsNoTracking()
                .Where(n => n.Scope == NoteScope.Member && visibleIds.Contains(n.ScopeRef))
                .OrderByDescending(n => n.Pinned).ThenByDescending(n => n.AuthoredAt)
                .Take(100)
                .ToListAsync(ct);

            var subjectId = gate.SubjectId;
            var hasSupervisor = principal.HasScope(PolicyPolicies.Supervise);

            var view = new AdministrativeThreeSixtyView(
                beneficiaryId, asOf,
                facts?.Record,
                memberships,
                family,
                [.. history.Select(h => new EnrollmentHistoryView(
                    h.EventId, h.EnrollmentId, h.EventType.ToString(), h.EffectiveDate, h.OccurredAt,
                    h.IsRetroEffective, h.Reason))],
                [.. documents.Select(d => new DocumentSummaryView(
                    d.LinkId, d.DocumentId, d.DocumentClass.ToString(), d.VisibilityClass.ToString(), d.Title,
                    d.DocumentDate, d.UploadedAt, d.UploadedByDisplay, d.Status.ToString(),
                    DocumentAccess.MayDownload(d.DocumentClass, d.VisibilityClass, principal.Roles)))],
                // Note bodies go through 19.3's rules: a caller without the class receives the note's
                // EXISTENCE and an explicit withheld flag, never a silently empty record.
                [.. notes.Select(n => NoteView.For(n, principal.Roles, subjectId, hasSupervisor))],
                unavailable,
                withheld);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "beneficiary", EntityId = beneficiaryId.ToString(), Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                DecisionOutcome = "administrative-360",
                DecisionReasonCode =
                    $"memberships:{memberships.Count};notes:{notes.Count};documents:{documents.Count}"
                    + (withheld.Count > 0 ? ";withheld" : "")
                    + (unavailable.Count > 0 ? ";partial" : ""),
                FieldClasses = ["coverage", "identity"],
            }, ct);

            return Results.Ok(view);
        });
    }

    // ---- The covered family ------------------------------------------------------------------------------

    /// <summary>
    /// Who else this cover reaches, asked from one membership.
    ///
    /// <para>The data was already here — <c>CoveredFamilyMemberView</c> has been a section of the 360 since
    /// 19.5, and nothing in the product ever called it. It is its own endpoint now because the question gets
    /// asked on its own: an officer with a mother at the desk needs to know whether the children are on the
    /// cover, and composing the notes, the documents and two hundred history rows to answer it is a lot of
    /// disclosure for one list.</para>
    ///
    /// <para>Names come from patient-service for THIS HOUSEHOLD only, through the caller's own token — a
    /// family is a handful of rows, so the roster's per-page batching applies unchanged.</para>
    /// </summary>
    private static void MapCoveredFamily(RouteGroupBuilder read)
    {
        read.MapGet("/enrollments/{enrollmentId:guid}/family", async (
            Guid enrollmentId,
            PolicyDbContext db, AdministrativeQuery query, IBeneficiaryAdministrativeSource patient,
            PolicyGate gate, IPayerDirectory payers, IAuditClient audit, HttpContext http,
            CancellationToken ct) =>
        {
            var principal = gate.Principal;
            if (principal is null) return GateResults.Unauthenticated();

            var (exists, _, root, payerId) = await query.EnrollmentHouseholdRootAsync(enrollmentId, ct);
            if (!exists) return ProblemResults.NotFound("ENROLLMENT_NOT_FOUND", "No such enrolment.");

            var permitted = await payers.GetAsync(principal, ct);
            if (PayerScopeRules.Check(permitted, payerId) == PayerScopeOutcome.Denied)
            {
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "enrollment", EntityId = enrollmentId.ToString(), Action = AuditAction.Read,
                    ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                    TenantId = principal.TenantId,
                    DecisionOutcome = "AccessDenied", DecisionReasonCode = "payer-not-permitted",
                }, ct);
                return GateResults.Forbidden("urn:hbmp:payer-scope-denied",
                    detail: "You are not permitted to read this member's household.", reason: "payer-not-permitted");
            }

            var household = await query.HouseholdAsync([root], ct);

            // A household CAN span policies — a dependant enrolled under one payer and the principal under
            // another is unusual but nothing forbids it. Rows behind a payer this caller may not read are
            // dropped and COUNTED, so a family of five never renders as three with no explanation.
            var scoped = new List<Enrollment>(household.Count);
            foreach (var member in household)
            {
                var (_, memberPayer) = await query.PolicyPayerAsync(member.PolicyId, ct);
                if (PayerScopeRules.Check(permitted, memberPayer) == PayerScopeOutcome.Allowed) scoped.Add(member);
            }

            var (members, namesUnavailable) = await ComposeHouseholdAsync(
                db, patient, http.Request.Headers.Authorization.FirstOrDefault(), scoped,
                subjectEnrollmentId: enrollmentId, ct);

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "enrollment", EntityId = enrollmentId.ToString(), Action = AuditAction.Read,
                ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                DecisionOutcome = "covered-family",
                DecisionReasonCode = $"members:{members.Count}"
                    + (household.Count > scoped.Count ? ";withheld" : "")
                    + (namesUnavailable ? ";partial" : ""),
                // A family list is names + membership. Saying so is what makes "who looked up this family"
                // answerable later.
                FieldClasses = ["identity", "coverage"],
            }, ct);

            return Results.Ok(new FamilyView(
                enrollmentId, members,
                namesUnavailable ? ["patient-service"] : [],
                Withheld: household.Count - scoped.Count));
        });
    }

    /// <summary>
    /// Principals and dependants around this member's memberships, from the enrolment graph.
    ///
    /// <para>Rooted on the principal (see <see cref="Household"/>) rather than walked one hop from the rows the
    /// caller already had. The one-hop version answered correctly from a principal and wrongly from a child:
    /// it reached the father and stopped, so a child's record listed no siblings and the omission looked like
    /// a family with one parent.</para>
    /// </summary>
    private static async Task<IReadOnlyList<CoveredFamilyMemberView>> CoveredFamilyAsync(
        PolicyDbContext db, AdministrativeQuery query, IBeneficiaryAdministrativeSource patient,
        string? token, IReadOnlyList<Enrollment> mine, CancellationToken ct)
    {
        if (mine.Count == 0) return [];

        var roots = Household.RootsOf(mine.Select(e => (e.EnrollmentId, e.PrincipalEnrollmentId)));
        var household = await query.HouseholdAsync(roots, ct);

        // The 360's family section is the family AROUND this person: their own memberships are the
        // Memberships section directly above it, and repeating them here would read as a duplicate.
        var mineIds = mine.Select(e => e.EnrollmentId).ToHashSet();
        var others = household.Where(e => !mineIds.Contains(e.EnrollmentId)).ToList();

        var (members, _) = await ComposeHouseholdAsync(db, patient, token, others, null, ct);
        return members;
    }

    /// <summary>Turn household enrolments into the view: plan labels from here, names from their owner.</summary>
    private static async Task<(IReadOnlyList<CoveredFamilyMemberView> Members, bool NamesUnavailable)>
        ComposeHouseholdAsync(
            PolicyDbContext db, IBeneficiaryAdministrativeSource patient, string? token,
            IReadOnlyList<Enrollment> household, Guid? subjectEnrollmentId, CancellationToken ct)
    {
        if (household.Count == 0) return ([], false);

        // The caller's own token, so patient-service applies ITS projection and writes ITS disclosure audit —
        // the same rule the 360 follows, for the same reason (see AdministrativeSeams).
        var summaries = await patient.SummariesAsync(
            [.. household.Select(e => e.BeneficiaryId).Distinct()], token, ct);

        var planIds = household.Select(e => e.PolicyPlanId).Distinct().ToList();
        var labels = await db.PolicyPlans.AsNoTracking()
            .Where(pp => planIds.Contains(pp.PolicyPlanId))
            .Select(pp => new { pp.PolicyPlanId, pp.PlanLabel })
            .ToDictionaryAsync(x => x.PolicyPlanId, x => x.PlanLabel, ct);

        var members = household.Select(e =>
        {
            var summary = summaries?.GetValueOrDefault(e.BeneficiaryId);
            return new CoveredFamilyMemberView(
                e.EnrollmentId, e.BeneficiaryId, e.MemberNo,
                summary?.GivenName, summary?.FamilyName,
                e.Relationship.ToString(), e.Status.ToString(),
                IsPrincipal: e.PrincipalEnrollmentId is null,
                PlanLabel: labels.GetValueOrDefault(e.PolicyPlanId),
                EffectiveFrom: e.EffectiveFrom, EffectiveTo: e.EffectiveTo,
                IsSubject: subjectEnrollmentId == e.EnrollmentId);
        }).ToList();

        // Null means patient-service could not be ASKED — the names are blank rather than absent, and the
        // caller is told which is which.
        return (members, summaries is null);
    }
}
