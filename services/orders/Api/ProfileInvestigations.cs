using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Auth.Authorization;
using Mersal.Authz;
using Mersal.Orders.Domain;
using Mersal.Orders.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Orders.Api;

/// <summary>
/// Phase 20 — the seam the patient profile's <c>investigations</c> section reads.
///
/// <para><b>This is the endpoint where the profile could most easily have become a bypass, so the gate lives
/// HERE and not in the aggregator.</b> Every line goes through the same
/// <see cref="SensitiveResultGate.Decide"/> call the single-result read uses: a non-Standard result is
/// existence-only for everyone except the authoring/ordering clinician or an active grant holder, and that
/// deliberately overrides the approval team's standing oversight (design 37 §6, design 39 §4 note *).</para>
///
/// <para>A restricted line therefore leaves this service with <c>restricted: true</c> and <b>no value</b>.
/// profile-service has nothing to redact, which is the point: an aggregator that receives values and is trusted
/// to drop them is one refactor away from not dropping them.</para>
/// </summary>
public static class ProfileInvestigationsEndpoint
{
    public static void MapProfileInvestigations(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/investigation-orders/for-beneficiary/{beneficiaryId:guid}", async (
            Guid beneficiaryId, string? scope, HttpRequest http, OrdersDbContext db,
            ITreatingRelationshipClient treating,
            IAuditClient audit, IHbmpPrincipalAccessor me, TimeProvider clock, CancellationToken ct) =>
        {
            var principal = me.Principal;
            if (principal is null) return GateResults.Unauthenticated();
            var subject = principal.Subject;

            // The shared design-39 §4 matrix, evaluated on the fact orders can resolve. OrdersPolicies.Read
            // stays exactly as narrow as it was (doctor + treating) and still guards every other read here.
            var treats = await treating.TreatsAsync(
                beneficiaryId, http.Headers.Authorization.FirstOrDefault(), ct);
            var denied = ProfileSeam.Check(
                principal, ProfileSeam.ContextFor(principal, treatingRelationship: treats),
                ProfileSections.Investigations);
            if (denied is not null) return denied;

            var orders = await db.Orders.AsNoTracking().Include(o => o.Lines)
                .Where(o => o.BeneficiaryId == beneficiaryId)
                .OrderByDescending(o => o.RequestedAt)
                .Take(200)
                .ToListAsync(ct);
            if (orders.Count == 0) return Results.Ok(new ProfileInvestigationsView([]));

            var lineIds = orders.SelectMany(o => o.Lines).Select(l => l.OrderLineId).ToList();

            var fulfillments = await db.Fulfillments.AsNoTracking()
                .Where(f => lineIds.Contains(f.OrderLineId))
                .ToListAsync(ct);

            // PROVIDER OWNERSHIP. `scope=own` is what a lab or imaging centre's profile asks for: it sees the
            // orders IT is fulfilling and no others. Applied here, under this service's own knowledge of who
            // consumed what — not by the aggregator, which would be a filter the owning service had stopped
            // applying.
            var ownOnly = string.Equals(scope, "own", StringComparison.OrdinalIgnoreCase);
            var callerProvider = Guid.TryParse(principal.ProviderId, out var pg) ? pg : Guid.Empty;
            if (ownOnly)
            {
                var mine = fulfillments.Where(f => f.PerformingProviderId == callerProvider)
                    .Select(f => f.OrderLineId).ToHashSet();
                orders = orders
                    .Where(o => o.Lines.Any(l => mine.Contains(l.OrderLineId)))
                    .ToList();
                foreach (var o in orders) o.Lines = [.. o.Lines.Where(l => mine.Contains(l.OrderLineId))];
            }

            var now = clock.GetUtcNow();
            var grantedLineIds = string.IsNullOrEmpty(subject)
                ? []
                : (await db.ReportAccessGrants.AsNoTracking()
                    .Where(g => g.GranteeUserId == subject && g.RevokedAt == null && now < g.ExpiresAt
                                && lineIds.Contains(g.OrderLineId))
                    .Select(g => g.OrderLineId)
                    .ToListAsync(ct)).ToHashSet();

            var rows = new List<ProfileInvestigationView>();
            var restrictedCount = 0;

            foreach (var order in orders)
            {
                var isAuthor = order.CreatedBy is not null && order.CreatedBy == subject;
                foreach (var line in order.Lines)
                {
                    var disclosure = SensitiveResultGate.Decide(
                        line.SensitivityLevel, isAuthor, grantedLineIds.Contains(line.OrderLineId));
                    var restricted = disclosure == ResultDisclosure.ExistenceOnly;
                    if (restricted) restrictedCount++;

                    var result = fulfillments
                        .Where(f => f.OrderLineId == line.OrderLineId && f.ResultUploadedAt is not null)
                        .OrderByDescending(f => f.ResultUploadedAt)
                        .FirstOrDefault();

                    rows.Add(new ProfileInvestigationView(
                        order.OrderNo,
                        line.OrderLineId,
                        line.Description ?? line.Code,
                        order.RequestedAt,
                        line.Status.ToString(),
                        result?.PerformingProviderId.ToString(),
                        // The ONE conditional field. A restricted line carries no value — not an empty string,
                        // not a placeholder: the field is absent from the JSON.
                        restricted ? null : result?.ResultValue,
                        restricted,
                        line.SensitivityLevel.ToString(),
                        OrderTypes.Canonical(order.OrderType).ToString(),   // 29.2
                        order.EncounterId));
                }
            }

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "investigation_orders", EntityId = beneficiaryId.ToString(), Action = AuditAction.Read,
                ActorUserId = subject,
                ActorRole = string.Join(',', principal.Roles),
                TenantId = principal.TenantId,
                Purpose = "patient-profile",
                DecisionOutcome = "ProfileInvestigationsRead",
                // The count of withheld results is recorded, because "how often is this person's mental-health
                // result being reached for" is a question a protection review genuinely asks.
                DecisionReasonCode = $"lines:{rows.Count};restricted:{restrictedCount};scope:{(ownOnly ? "own" : "all")}",
                FieldClasses = restrictedCount > 0 ? ["result", "sensitive"] : ["result"],
                Severity = AuditSeverity.Notice,
            }, ct);

            return Results.Ok(new ProfileInvestigationsView(rows));
        }).RequireAuthorization(HbmpPolicies.Scope("profile:read"))
        .Produces<ProfileInvestigationsView>();
    }
}

/// <summary>One ordered investigation. <c>ResultSummary</c> is null whenever <c>Restricted</c> is true — the
/// design-37 §6 decision, made here and not downstream.</summary>
public sealed record ProfileInvestigationView(
    string OrderNo, Guid LineId, string Category, DateTimeOffset OrderedOn, string Status,
    string? ProviderName, string? ResultSummary, bool Restricted, string? SensitivityLevel,
    /// <summary>29.2 — the ORDER TYPE (Lab / Radiology / Procedure), so the history can be read by the kind
    /// of service rather than as one flat list (design 45 §3).
    ///
    /// <para>Additive on the EXISTING section, not a new one, and that is the point: a procedure IS an
    /// investigation order, so it already travels this path under this gate. Splitting the view by a routing
    /// label the caller has already been authorised to see adds no access, which is what "no new access
    /// path" has to mean in practice. An order type carries no clinical content — it says which queue the
    /// request went to, not what was wrong with the patient.</para></summary>
    string OrderType,
    /// <summary>The encounter the order was raised on.
    ///
    /// <para>Carried so a caller reading ONE visit can tell which of a member's orders belong to it. Without
    /// it the profile's investigation list is a flat history of everything ever ordered, and "what did this
    /// consultation actually order?" is a question nobody can answer from it — which is the question a
    /// clinician opening a past visit is usually asking. It is an id and nothing more: it discloses no
    /// clinical content, and a caller who cannot read the encounter still cannot.</para></summary>
    Guid EncounterId);

public sealed record ProfileInvestigationsView(IReadOnlyList<ProfileInvestigationView> Items);
