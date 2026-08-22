using Mersal.Audit.Client;
using Mersal.Auth;
using Mersal.Authz;
using Mersal.Events;
using Mersal.Policy.Domain;
using Mersal.Policy.Infrastructure;
using Mersal.Time;
using Microsoft.EntityFrameworkCore;

namespace Mersal.Policy.Api;

/// <summary>
/// Phase 19.7 — payer administration (design 56).
///
/// <para><b>What changed.</b> The payer surface was create + list + get-by-id, over a row that held a code,
/// two names, a type and a status. So a payer could be brought into existence and never afterwards corrected,
/// switched off, or explained: a typo in a donor's name was permanent, an ended grant stayed indistinguishable
/// from a live one, and "who raised this ceiling" had no answer because there was no ceiling to raise. This
/// file adds the three writes that make it a record rather than an entry, plus the two reads that make those
/// writes reviewable.</para>
///
/// <para><b>Deactivation refuses rather than cascades.</b> A payer with live policies funding live members is
/// not a row to switch off — every one of those policies would keep resolving against a counterparty the
/// platform has been told is finished, and nothing downstream would say so. So the write is refused with the
/// COUNT, and the administrator is sent to do the thing they actually meant: end the policies, or leave the
/// payer active until they have. Cascading would have been the convenient choice and would have made a
/// four-word confirmation dialog end thousands of people's cover.</para>
///
/// <para><b>Payer scope is enforced here, not only on the query surface.</b> 19.5 restricts a user to a set of
/// payers and <c>policy-query</c>/<c>member-query</c> honour it — but <c>GET /payers</c> did not, so a user
/// restricted to one donor could read the whole counterparty list, and (from this phase) its commercial terms.
/// The restriction narrows the list and refuses a named payer outside it with 403, exactly as
/// <see cref="QueryEndpoints"/> does, and for the same reason: an empty page reads as "no such payer", which
/// is a different and misleading answer.</para>
/// </summary>
public static class PayerEndpoints
{
    public static void MapPayers(RouteGroupBuilder v1)
    {
        MapCreate(v1);
        MapRead(v1);
        MapUpdate(v1);
        MapStatus(v1);
        MapHistory(v1);
    }

    // ---- create ------------------------------------------------------------------------------------------

    private static void MapCreate(RouteGroupBuilder v1)
    {
        v1.MapPost("/payers", async (CreatePayer req, PolicyDbContext db, PolicyGate gate, IAuditClient audit,
            IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(req);
            var denied = await gate.CheckAsync(PolicyPolicies.Admin, ct);
            if (denied is not null) return denied;
            if (!Enum.TryParse<PayerType>(req.PayerType, out var type))
                return ProblemResults.Invalid("UNKNOWN_PAYER_TYPE", $"'{req.PayerType}' is not a payer type.");
            if (string.IsNullOrWhiteSpace(req.PayerCode))
                return ProblemResults.Invalid("PAYER_CODE_REQUIRED", "A payer code is required.");

            var now = clock.GetUtcNow();
            var payer = new Payer
            {
                PayerId = Guid.NewGuid(), PayerCode = req.PayerCode.Trim(),
                NameEn = req.NameEn, NameAr = req.NameAr, PayerType = type,
                Contact = PayerContactCodec.Write(req.Contacts),
                Notes = Blank(req.Notes),
                CreatedAt = now, UpdatedAt = now,
                CreatedBy = gate.SubjectId, UpdatedBy = gate.SubjectId,
                CreatedByName = gate.Principal?.DisplayName, UpdatedByName = gate.Principal?.DisplayName,
            };
            if (ApplyTerms(payer, req.Terms) is { } bad) return bad;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Payers.Add(payer);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "payer", EntityId = payer.PayerId.ToString(),
                Action = AuditAction.Create, ActorUserId = gate.Subject,
            }, ct);
            await outbox.EnqueueAsync("PayerCreated", "policy.events",
                new
                {
                    tenantId = payer.TenantId, payerId = payer.PayerId, payer.PayerCode,
                    payerType = type.ToString(),
                    // The NAMES, so the dashboard can label a payer instead of printing eight characters of
                    // its uuid. reporting-service keeps a dimension-label table for exactly this and had no
                    // feed for it; `AnalyticsQueries.Label` falls back to `id.ToString()[..8]` — deliberately,
                    // because a truncated id sends someone looking while "Unknown payer" hides the gap.
                    payer.NameEn, payer.NameAr,
                }, ct);
            await tx.CommitAsync(ct);

            return Results.Created($"/api/v1/payers/{payer.PayerId}", View(payer, gate, clock));
        })
        .Produces<PayerView>();
    }

    // ---- read --------------------------------------------------------------------------------------------

    private static void MapRead(RouteGroupBuilder v1)
    {
        v1.MapGet("/payers", async (
            PolicyDbContext db, PolicyGate gate, IPayerDirectory directory, TimeProvider clock,
            CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Read, ct);
            if (denied is not null) return denied;
            var principal = gate.Principal!;

            var permitted = await directory.GetAsync(principal, ct);
            var q = db.Payers.AsNoTracking().Where(p => !p.IsDeleted);
            if (!permitted.IsUnrestricted)
            {
                var ids = permitted.PayerIds.ToList();
                q = q.Where(p => ids.Contains(p.PayerId));
            }

            var rows = await q.OrderBy(p => p.PayerCode).ToListAsync(ct);
            var today = BusinessCalendar.DateIn(clock.GetUtcNow());
            var mayContract = AdministrativeProjection.MayReadContract(principal.Roles);
            return Results.Ok(rows.ConvertAll(p => PayerView.From(p, today, mayContract)));
        })
        .Produces<IEnumerable<PayerView>>();

        // The detail read. It answers "who is this payer" and "what is riding on them" together, because the
        // second is why anybody opens the first: a ceiling with no committed total beside it is a number
        // nobody can act on, and two round trips to put them side by side is two chances to show a stale half.
        v1.MapGet("/payers/{id:guid}", async (
            Guid id, PolicyDbContext db, PolicyGate gate, IPayerDirectory directory, IAuditClient audit,
            TimeProvider clock, CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Read, ct);
            if (denied is not null) return denied;
            var principal = gate.Principal!;

            var permitted = await directory.GetAsync(principal, ct);
            if (!permitted.Allows(id))
            {
                await ScopeDenied(audit, principal, id, ct);
                return GateResults.Forbidden("urn:hbmp:payer-scope-denied",
                    detail: "You are not permitted to read this payer.", reason: "payer-not-permitted");
            }

            var payer = await db.Payers.AsNoTracking().FirstOrDefaultAsync(p => p.PayerId == id && !p.IsDeleted, ct);
            if (payer is null) return NotFound();

            var book = await BookAsync(db, id, AdministrativeProjection.MayReadAmounts(principal.Roles),
                payer.FundingCeiling, ct);
            return Results.Ok(new PayerDetailView(View(payer, gate, clock), book));
        })
        .Produces<PayerDetailView>();
    }

    /// <summary>
    /// The payer's book of business, in ONE round trip of grouped aggregates rather than a query per number.
    ///
    /// <para>Counts always; amounts only for a caller entitled to them. The COMMITTED-versus-ceiling
    /// percentage survives either way, following the policy query surface's rule — "this grant is 92%
    /// committed" is an operational fact, the pounds behind it are a commercial one.</para>
    /// </summary>
    private static async Task<PayerBookView> BookAsync(
        PolicyDbContext db, Guid payerId, bool mayReadAmounts, decimal? ceiling, CancellationToken ct)
    {
        var policies = db.Policies.AsNoTracking().Where(p => p.PayerId == payerId && !p.IsDeleted);

        var policyCount = await policies.CountAsync(ct);
        var activePolicyCount = await policies.CountAsync(p => p.Status == PolicyStatus.Active, ct);

        var members = db.Enrollments.AsNoTracking().Where(e => policies.Any(p => p.PolicyId == e.PolicyId));
        var memberCount = await members.CountAsync(ct);
        var activeMemberCount = await members.CountAsync(e => e.Status == EnrollmentStatus.Active, ct);

        var planCount = await db.PolicyPlans.AsNoTracking()
            .Where(pp => policies.Any(p => p.PolicyId == pp.PolicyId))
            .Select(pp => pp.PlanVersionId).Distinct().CountAsync(ct);

        // Coverage limits are the entitlements GENERATED under this payer's policies — the committed money.
        var limits = db.CoverageLimits.AsNoTracking()
            .Where(l => db.Coverages.Any(c => c.CoverageId == l.CoverageId && !c.IsDeleted
                                              && policies.Any(p => p.PolicyId == c.PolicyId)));

        decimal? committed = null, consumed = null;
        // Summed unconditionally — the percentage below needs the total even when the caller may not read it.
        // `SumAsync` over an empty set returns 0, which is the honest answer for a payer nobody is enrolled
        // under; null is reserved for "you may not see this".
        var committedRaw = await limits.SumAsync(l => (decimal?)l.LimitValue, ct) ?? 0m;
        var consumedRaw = await limits.SumAsync(l => (decimal?)l.ConsumedValue, ct) ?? 0m;
        if (mayReadAmounts) { committed = committedRaw; consumed = consumedRaw; }

        decimal? percent = ceiling is > 0m
            ? Math.Round(committedRaw / ceiling.Value * 100m, 1, MidpointRounding.AwayFromZero)
            : null;

        return new PayerBookView(policyCount, activePolicyCount, memberCount, activeMemberCount, planCount,
            committed, consumed, percent);
    }

    // ---- update ------------------------------------------------------------------------------------------

    private static void MapUpdate(RouteGroupBuilder v1)
    {
        v1.MapPut("/payers/{id:guid}", async (
            Guid id, UpdatePayer req, PolicyDbContext db, PolicyGate gate, IPayerDirectory directory,
            IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(req);
            var denied = await gate.CheckAsync(PolicyPolicies.Admin, ct);
            if (denied is not null) return denied;
            var principal = gate.Principal!;

            if (!(await directory.GetAsync(principal, ct)).Allows(id))
            {
                await ScopeDenied(audit, principal, id, ct);
                return GateResults.Forbidden("urn:hbmp:payer-scope-denied",
                    detail: "You are not permitted to administer this payer.", reason: "payer-not-permitted");
            }

            if (!Enum.TryParse<PayerType>(req.PayerType, out var type))
                return ProblemResults.Invalid("UNKNOWN_PAYER_TYPE", $"'{req.PayerType}' is not a payer type.");
            if (string.IsNullOrWhiteSpace(req.NameEn) || string.IsNullOrWhiteSpace(req.NameAr))
                return ProblemResults.Invalid("PAYER_NAME_REQUIRED",
                    "A payer needs a name in both languages: half the platform renders in Arabic.");

            var payer = await db.Payers.FirstOrDefaultAsync(p => p.PayerId == id && !p.IsDeleted, ct);
            if (payer is null) return NotFound();

            var before = Signature(payer);
            payer.NameEn = req.NameEn.Trim();
            payer.NameAr = req.NameAr.Trim();
            payer.PayerType = type;
            payer.Contact = PayerContactCodec.Write(req.Contacts);
            payer.Notes = Blank(req.Notes);
            if (ApplyTerms(payer, req.Terms) is { } bad) return bad;
            Stamp(payer, gate, clock);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

            await audit.EmitAsync(new AuditEventDraft
            {
                EntityType = "payer", EntityId = payer.PayerId.ToString(),
                Action = AuditAction.Update, ActorUserId = gate.Subject,
                DecisionOutcome = "payer-updated", DecisionReasonCode = before,
            }, ct);
            // The names are on the event for the same reason they are on PayerCreated: reporting-service
            // labels a payer from this feed, and a rename that never reaches it leaves every dashboard
            // showing the old name with no way to tell that it is stale.
            await outbox.EnqueueAsync("PayerUpdated", "policy.events", new
            {
                tenantId = payer.TenantId, payerId = payer.PayerId, payer.PayerCode,
                payerType = type.ToString(), payer.NameEn, payer.NameAr,
            }, ct);
            await tx.CommitAsync(ct);

            return Results.Ok(View(payer, gate, clock));
        })
        .Produces<PayerView>();
    }

    // ---- deactivate / reactivate -------------------------------------------------------------------------

    private static void MapStatus(RouteGroupBuilder v1)
    {
        v1.MapPost("/payers/{id:guid}/deactivate", async (
            Guid id, ChangePayerStatus req, PolicyDbContext db, PolicyGate gate, IPayerDirectory directory,
            IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
            await SetStatusAsync(id, req, CatalogStatus.Inactive, db, gate, directory, audit, outbox, clock, ct))
        .Produces<PayerView>();

        v1.MapPost("/payers/{id:guid}/reactivate", async (
            Guid id, ChangePayerStatus req, PolicyDbContext db, PolicyGate gate, IPayerDirectory directory,
            IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct) =>
            await SetStatusAsync(id, req, CatalogStatus.Active, db, gate, directory, audit, outbox, clock, ct))
        .Produces<PayerView>();
    }

    private static async Task<IResult> SetStatusAsync(
        Guid id, ChangePayerStatus req, CatalogStatus target, PolicyDbContext db, PolicyGate gate,
        IPayerDirectory directory, IAuditClient audit, IOutbox outbox, TimeProvider clock, CancellationToken ct)
    {
        var denied = await gate.CheckAsync(PolicyPolicies.Admin, ct);
        if (denied is not null) return denied;
        var principal = gate.Principal!;

        if (!(await directory.GetAsync(principal, ct)).Allows(id))
        {
            await ScopeDenied(audit, principal, id, ct);
            return GateResults.Forbidden("urn:hbmp:payer-scope-denied",
                detail: "You are not permitted to administer this payer.", reason: "payer-not-permitted");
        }

        var reason = req?.Reason?.Trim();
        // Ten characters, matching the platform's other mandatory reasons. Not to be pedantic — a one-word
        // reason ("old") is indistinguishable from no reason at all to the person reading it next year, and
        // the whole point of requiring one is that it be readable then.
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10)
            return ProblemResults.Invalid("PAYER_STATUS_REASON_REQUIRED",
                "Say why, in a sentence somebody reading this record next year would understand.");

        var payer = await db.Payers.FirstOrDefaultAsync(p => p.PayerId == id && !p.IsDeleted, ct);
        if (payer is null) return NotFound();
        if (payer.Status == target)
            return ProblemResults.Conflict("PAYER_ALREADY_IN_STATUS", $"This payer is already {target}.");

        // THE REFUSAL. See the class remarks: a payer still funding live policies is not a row to switch off.
        if (target == CatalogStatus.Inactive)
        {
            var live = await db.Policies.AsNoTracking()
                .CountAsync(p => p.PayerId == id && !p.IsDeleted && p.Status == PolicyStatus.Active, ct);
            if (live > 0)
            {
                await audit.EmitAsync(new AuditEventDraft
                {
                    EntityType = "payer", EntityId = id.ToString(), Action = AuditAction.Update,
                    ActorUserId = principal.Subject, TenantId = principal.TenantId,
                    DecisionOutcome = "deactivation-refused", DecisionReasonCode = $"active-policies:{live}",
                }, ct);
                return ProblemResults.Conflict("PAYER_HAS_ACTIVE_POLICIES",
                    $"This payer still funds {live} active " + (live == 1 ? "policy" : "policies") +
                    ". End or transfer them first — deactivating the payer would leave them resolving against a " +
                    "counterparty the platform has been told is finished, and nothing downstream would say so.");
            }
        }

        var before = Signature(payer);
        var now = clock.GetUtcNow();
        payer.Status = target;
        payer.StatusReason = reason;
        payer.StatusChangedAt = now;
        payer.StatusChangedBy = gate.SubjectId;
        Stamp(payer, gate, clock);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await SaveOrConflict(db, ct) is { } conflict) return conflict;

        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "payer", EntityId = payer.PayerId.ToString(),
            Action = AuditAction.StateChange, ActorUserId = gate.Subject,
            DecisionOutcome = target == CatalogStatus.Active ? "reactivated" : "deactivated",
            DecisionReasonCode = before,
        }, ct);
        await outbox.EnqueueAsync(
            target == CatalogStatus.Active ? "PayerReactivated" : "PayerDeactivated", "policy.events",
            new { tenantId = payer.TenantId, payerId = payer.PayerId, payer.PayerCode, reason }, ct);
        await tx.CommitAsync(ct);

        return Results.Ok(View(payer, gate, clock));
    }

    // ---- history -----------------------------------------------------------------------------------------

    private static void MapHistory(RouteGroupBuilder v1)
    {
        // NOT the audit trail. The audit chain is hash-linked, tamper-evident, and readable only by Security,
        // Compliance and the DPO — widening it so a policy administrator can ask who raised a ceiling would
        // hand them the whole compliance record to answer a question about a row they own. This reads the
        // history twin the 0020 trigger writes, under the same authority and the same payer scope as the
        // payer itself. Both stores are written on every change; they answer different people.
        v1.MapGet("/payers/{id:guid}/history", async (
            Guid id, PolicyDbContext db, PolicyGate gate, IPayerDirectory directory, IAuditClient audit,
            CancellationToken ct) =>
        {
            var denied = await gate.CheckAsync(PolicyPolicies.Admin, ct);
            if (denied is not null) return denied;
            var principal = gate.Principal!;

            if (!(await directory.GetAsync(principal, ct)).Allows(id))
            {
                await ScopeDenied(audit, principal, id, ct);
                return GateResults.Forbidden("urn:hbmp:payer-scope-denied",
                    detail: "You are not permitted to read this payer's history.", reason: "payer-not-permitted");
            }

            if (!await db.Payers.AsNoTracking().AnyAsync(p => p.PayerId == id, ct)) return NotFound();

            var mayContract = AdministrativeProjection.MayReadContract(principal.Roles);
            var rows = await db.PayerHistory.AsNoTracking()
                .Where(h => h.PayerId == id)
                .OrderByDescending(h => h.HistoryId)
                .Take(200)
                .ToListAsync(ct);

            return Results.Ok(new PayerHistoryPage(id,
                rows.ConvertAll(r => PayerHistoryEntryView.From(r, mayContract))));
        })
        .Produces<PayerHistoryPage>();
    }

    // ---- shared ------------------------------------------------------------------------------------------

    private static PayerView View(Payer p, PolicyGate gate, TimeProvider clock) =>
        PayerView.From(p, BusinessCalendar.DateIn(clock.GetUtcNow()),
            AdministrativeProjection.MayReadContract(gate.Principal?.Roles ?? new HashSet<string>()));

    private static void Stamp(Payer p, PolicyGate gate, TimeProvider clock)
    {
        p.UpdatedAt = clock.GetUtcNow();
        p.UpdatedBy = gate.SubjectId;
        p.UpdatedByName = gate.Principal?.DisplayName;
    }

    /// <summary>What the row said before this write, compact enough to sit on the audit event's reason code.
    /// The history twin holds the full snapshot; this is what makes the AUDIT entry self-describing without
    /// a join into a store its reader may not have.</summary>
    private static string Signature(Payer p) =>
        $"{p.NameEn}|{p.PayerType}|{p.Status}|{p.FundingCeiling?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-"}" +
        $"|{p.AgreementFrom:yyyy-MM-dd}..{p.AgreementTo:yyyy-MM-dd}";

    /// <summary>Validates and applies the terms block. Returns a problem when the input cannot be a set of
    /// terms — the database refuses the same things (0020 CHECKs), and this is the half that explains why.</summary>
    private static IResult? ApplyTerms(Payer p, PayerTermsInput? t)
    {
        if (t is null)
        {
            // An update that names no terms CLEARS them, rather than leaving whatever was there. A partial
            // write that silently keeps old values is how a payer ends up with last year's ceiling and this
            // year's window, and no screen able to say which of the two was actually intended.
            p.ExternalRef = p.AgreementNo = null;
            p.AgreementFrom = p.AgreementTo = null;
            p.FundingCeiling = null;
            p.SettlementTermsDays = p.ClaimSubmissionWindowDays = null;
            p.InvoicingCadence = null;
            return null;
        }

        if (t.FundingCeiling is { } ceiling && ceiling <= 0m)
            return ProblemResults.Invalid("INVALID_FUNDING_CEILING",
                "A ceiling of zero is not 'uncapped', it is 'funded for nothing'. Leave it empty for uncapped.");
        if (t.AgreementFrom is { } from && t.AgreementTo is { } to && to <= from)
            return ProblemResults.Invalid("INVALID_AGREEMENT_WINDOW",
                "The agreement must end after it starts. The end date is exclusive.");
        if (t.SettlementTermsDays is < 0 or > 365)
            return ProblemResults.Invalid("INVALID_SETTLEMENT_TERMS", "Settlement terms are 0–365 days.");
        if (t.ClaimSubmissionWindowDays is < 0 or > 1095)
            return ProblemResults.Invalid("INVALID_SUBMISSION_WINDOW", "The claim submission window is 0–1095 days.");

        PayerInvoicingCadence? cadence = null;
        if (!string.IsNullOrWhiteSpace(t.InvoicingCadence))
        {
            if (!Enum.TryParse<PayerInvoicingCadence>(t.InvoicingCadence, out var parsed))
                return ProblemResults.Invalid("UNKNOWN_INVOICING_CADENCE",
                    $"'{t.InvoicingCadence}' is not an invoicing cadence.");
            cadence = parsed;
        }

        var currency = string.IsNullOrWhiteSpace(t.Currency) ? "EGP" : t.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3)
            return ProblemResults.Invalid("INVALID_CURRENCY", "A currency is a three-letter ISO 4217 code.");

        p.ExternalRef = Blank(t.ExternalRef);
        p.AgreementNo = Blank(t.AgreementNo);
        p.AgreementFrom = t.AgreementFrom;
        p.AgreementTo = t.AgreementTo;
        p.FundingCeiling = t.FundingCeiling;
        p.Currency = currency;
        p.SettlementTermsDays = t.SettlementTermsDays;
        p.InvoicingCadence = cadence;
        p.ClaimSubmissionWindowDays = t.ClaimSubmissionWindowDays;
        return null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static async Task ScopeDenied(IAuditClient audit, HbmpPrincipal principal, Guid payerId, CancellationToken ct) =>
        await audit.EmitAsync(new AuditEventDraft
        {
            EntityType = "payer", EntityId = payerId.ToString(), Action = AuditAction.Grant,
            ActorUserId = principal.Subject, ActorRole = string.Join(',', principal.Roles),
            TenantId = principal.TenantId,
            DecisionOutcome = "PayerScopeDenied", DecisionReasonCode = "payer-not-permitted",
        }, ct);

    private static IResult NotFound() =>
        Results.Problem(statusCode: 404, title: "Not Found", type: "https://mersal.foundation/problems/not-found");

    private static async Task<IResult?> SaveOrConflict(PolicyDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        // Only the states we can explain are translated; anything else keeps its stack and becomes a 500,
        // because a database error we have not reasoned about is not the client's mistake to be told about.
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg
                                           && pg.SqlState is "23505" or "23514")
        {
            return ((Npgsql.PostgresException)ex.InnerException!).SqlState == "23505"
                ? ProblemResults.Conflict("DUPLICATE_KEY", "A payer with this code already exists.")
                : ProblemResults.Invalid("INVALID_PAYER", "The payer's terms are not a valid set of terms.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return ProblemResults.Conflict("PAYER_CHANGED",
                "Somebody else changed this payer while you were editing it. Reload and reapply your change.");
        }
    }
}

/// <summary>A page of a payer's own change history. A record rather than an anonymous object so the response
/// has a name in the OpenAPI spec — a history the schema gate cannot describe is one no client can be
/// generated against.</summary>
public sealed record PayerHistoryPage(Guid PayerId, IReadOnlyList<PayerHistoryEntryView> Entries);
